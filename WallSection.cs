using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RetainingWallRebar
{
    /// <summary>
    /// Sistema local de la seccion:
    ///   DirW = eje longitudinal del muro (horizontal)
    ///   DirU = horizontal perpendicular al muro (ancho de zapata)
    ///   DirV = vertical (Z)
    /// El origen esta en la esquina (uMin, vMin, wMin) del bounding box local,
    /// de modo que todas las coordenadas locales van de 0 a Len*.
    /// Todas las magnitudes en pies (unidades internas de Revit).
    ///
    /// REQUISITO: el solido tiene que ser un PRISMA RECTO a lo largo de DirW, es decir,
    /// la misma seccion (u,v) en todo el recorrido. Todo lo demas (leer la seccion en
    /// la rebanada central, extender los arrays por LenW, usar LenU como ancho de
    /// zapata) da eso por hecho. Probe lo comprueba ANTES de leer nada mas y rechaza
    /// el elemento entero si no se cumple: un esquinero en L, un contrafuerte o un
    /// extremo a inglete acabarian con barras fuera del hormigon.
    /// </summary>
    public class WallSection
    {
        public XYZ Origin, DirU, DirV, DirW;
        public double LenU, LenV, LenW;

        /// <summary>Cota local (v) de la cara superior de la zapata.</summary>
        public double FootingTop;

        /// <summary>Caras del alzado en arranque (v = FootingTop) y en coronacion (v = LenV).</summary>
        public double StemU0Bot, StemU1Bot, StemU0Top, StemU1Top;

        public Solid HostSolid;

        /// <summary>Motivo del ultimo fallo de Probe, para poder diagnosticar desde el dialogo.</summary>
        public static string LastError;

        public XYZ P(double u, double v, double w) => Origin + DirU * u + DirV * v + DirW * w;

        /// <summary>Coordenadas locales (u,v,w) de un punto del modelo.</summary>
        public XYZ ToLocal(XYZ p)
        {
            XYZ d = p - Origin;
            return new XYZ(d.DotProduct(DirU), d.DotProduct(DirV), d.DotProduct(DirW));
        }

        /// <summary>Punto del modelo expresado en coordenadas locales, en mm, para mensajes.</summary>
        public string LocalMm(XYZ p)
        {
            XYZ l = ToLocal(p);
            return "u=" + ToMm(l.X) + " v=" + ToMm(l.Y) + " w=" + ToMm(l.Z) + " mm";
        }

        /// <summary>Pies -> mm redondeados, para mensajes.</summary>
        public static double ToMm(double ft) =>
            Math.Round(UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters));

        private static double Round(double ft) => ToMm(ft);

        private static string Dump(WallSection s) =>
            $"leido B={Round(s.LenU)} H={Round(s.LenV)} L={Round(s.LenW)}:";

        /// <summary>Cara u=min del alzado interpolada a la altura v (extrapola dentro de la zapata).</summary>
        public double FaceU0(double v)
        {
            double h = LenV - FootingTop;
            if (h < 1e-9) return StemU0Bot;
            double t = (v - FootingTop) / h;
            return StemU0Bot + (StemU0Top - StemU0Bot) * t;
        }

        public double FaceU1(double v)
        {
            double h = LenV - FootingTop;
            if (h < 1e-9) return StemU1Bot;
            double t = (v - FootingTop) / h;
            return StemU1Bot + (StemU1Top - StemU1Bot) * t;
        }

        /// <summary>True si el vuelo mayor de zapata esta del lado u=0 (talon a u minimo).</summary>
        public bool HeelAtU0 => StemU0Bot > (LenU - StemU1Bot);

        public string Describe()
        {
            double mm(double ft) => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters);
            return $"B={mm(LenU):0} H={mm(LenV):0} L={mm(LenW):0} " +
                   $"canto zap={mm(FootingTop):0} " +
                   $"alzado {mm(StemU1Bot - StemU0Bot):0}->{mm(StemU1Top - StemU0Top):0}";
        }

        // ------------------------------------------------------------------
        public static WallSection Probe(Document doc, Element host, AppConfig cfg)
        {
            LastError = null;

            List<Solid> solids = Solids(host);
            if (solids.Count == 0)
            { LastError = "no se encontro solido en el elemento"; return null; }

            // Si la familia tiene varias extrusiones sin unir, solo se veria la mayor y
            // se armaria un trozo del muro creyendo que es el muro entero. Se rechaza.
            Solid solid = solids[0];
            int significant = solids.Count(x => x.Volume > solid.Volume * 0.001);
            if (significant > 1)
            {
                LastError = "RECHAZADO, el elemento tiene " + significant + " solidos independientes y el plugin " +
                            "solo sabe leer uno; une las extrusiones en la familia (Unir geometria) o revisa el elemento. " +
                            "No se ha creado ninguna barra.";
                return null;
            }

            List<XYZ> pts = Vertices(solid);
            if (pts.Count < 4)
            { LastError = "el solido no tiene aristas legibles"; return null; }

            var ov = cfg.OverrideFor(TypeNameOf(doc, host));

            // --- eje longitudinal ---
            XYZ dirW = LongAxis(host, pts, ov);
            XYZ dirV = XYZ.BasisZ;
            XYZ dirU = dirV.CrossProduct(dirW).Normalize();

            double u0 = pts.Min(p => p.DotProduct(dirU)), u1 = pts.Max(p => p.DotProduct(dirU));
            double v0 = pts.Min(p => p.DotProduct(dirV)), v1 = pts.Max(p => p.DotProduct(dirV));
            double w0 = pts.Min(p => p.DotProduct(dirW)), w1 = pts.Max(p => p.DotProduct(dirW));

            var s = new WallSection
            {
                DirU = dirU,
                DirV = dirV,
                DirW = dirW,
                Origin = dirU * u0 + dirV * v0 + dirW * w0,
                LenU = u1 - u0,
                LenV = v1 - v0,
                LenW = w1 - w0,
                HostSolid = solid
            };

            // --- SEGURIDAD PRIMERO: el solido tiene que ser un prisma recto ---
            // Se comprueba antes de leer canto, caras o nada mas. Si la seccion no es
            // constante a lo largo del eje, el bounding box no describe el hormigon y
            // cualquier barra que se generase podria quedar en el aire (hueco de una L).
            if (!IsRightPrism(s, cfg, out string why))
            {
                LastError = "RECHAZADO, el solido no es un prisma recto (la seccion no es constante a lo largo del eje): " +
                            why + ". Los muros esquineros en L, los contrafuertes, los escalones y los extremos a " +
                            "inglete no estan soportados (ver README). No se ha creado ninguna barra.";
                return null;
            }

            double slice = Mm(cfg.ProbeSliceMm);
            double wMid = s.LenW * 0.5;
            double wa = Math.Max(0, wMid - slice), wb = Math.Min(s.LenW, wMid + slice);

            // --- canto de zapata ---
            if (ov.FootingThicknessMm > 0)
            {
                s.FootingTop = Mm(ov.FootingThicknessMm);
            }
            else
            {
                double? tA = TopOfSliceAtU(s, 0, slice, wa, wb);
                double? tB = TopOfSliceAtU(s, s.LenU - slice, s.LenU, wa, wb);
                var vals = new List<double>();
                if (tA.HasValue) vals.Add(tA.Value);
                if (tB.HasValue) vals.Add(tB.Value);
                if (vals.Count == 0)
                { LastError = Dump(s) + " las rebanadas laterales salieron vacias"; return null; }
                // el vuelo libre de zapata da el canto; el lado donde arranca el alzado da la altura total
                s.FootingTop = vals.Min();
            }
            if (s.FootingTop <= 0 || s.FootingTop >= s.LenV)
            {
                LastError = Dump(s) + " canto de zapata incoherente (" + Round(s.FootingTop) + " mm)";
                return null;
            }

            // --- caras del alzado en arranque y coronacion ---
            var bot = StemExtentsAt(s, s.FootingTop + slice * 0.5, slice, wa, wb);
            var top = StemExtentsAt(s, s.LenV - slice * 1.5, slice, wa, wb);
            if (bot == null || top == null)
            { LastError = Dump(s) + " no se leyeron las caras del alzado"; return null; }

            s.StemU0Bot = bot.Item1; s.StemU1Bot = bot.Item2;
            s.StemU0Top = top.Item1; s.StemU1Top = top.Item2;
            return s;
        }

        // ==================================================================
        // Comprobacion de prisma recto (seccion constante a lo largo del eje)
        // ==================================================================

        /// <summary>
        /// True solo si la seccion transversal es la misma en todo el recorrido del eje.
        /// Son dos comprobaciones independientes y las dos tienen que pasar:
        ///  1. Muestreo: se corta el solido con rebanadas finas en muchas estaciones del
        ///     recorrido y se compara cada seccion (extension, area y contorno) con la
        ///     seccion central. Es la comprobacion directa: si en alguna estacion la
        ///     seccion es distinta, no es un prisma.
        ///  2. Caras: en un prisma recto toda cara es paralela al eje o es una tapa en
        ///     uno de los dos extremos. Una cara perpendicular al eje en el interior
        ///     del recorrido (el rincon de una L, un contrafuerte, un escalon) o una
        ///     cara oblicua (inglete) delatan que no lo es. Es exacta y no depende del
        ///     paso de muestreo, asi que cubre detalles mas estrechos que el paso.
        /// No hay forma de desactivarla desde config.json: es preferible negarse a
        /// armar una pieza a armarla mal.
        /// </summary>
        private static bool IsRightPrism(WallSection s, AppConfig cfg, out string why)
        {
            double tol = Mm(cfg.PrismCheckToleranceMm);
            if (tol <= 0) tol = Mm(2);

            if (!SectionIsConstant(s, cfg, tol, out why)) return false;
            if (!FacesAreParallelOrEndCaps(s, tol, out why)) return false;
            return true;
        }

        /// <summary>Seccion (u,v) del solido en una estacion w del recorrido.</summary>
        private sealed class SectionSample
        {
            public double W;
            /// <summary>Area de la seccion y area de cada una de las dos tapas de la rebanada.</summary>
            public double Area, AreaA, AreaB;
            public double U0 = double.MaxValue, U1 = double.MinValue;
            public double V0 = double.MaxValue, V1 = double.MinValue;
            public List<UV> Vertices = new List<UV>();
            public List<(UV a, UV b)> Segments = new List<(UV a, UV b)>();

            public string Describe() =>
                "u " + ToMm(U0) + ".." + ToMm(U1) + ", v " + ToMm(V0) + ".." + ToMm(V1) + " mm, area " +
                UnitUtils.ConvertFromInternalUnits(Area, UnitTypeId.SquareMeters).ToString("0.000") + " m2";
        }

        private static bool SectionIsConstant(WallSection s, AppConfig cfg, double tol, out string why)
        {
            why = null;

            double step = Mm(cfg.PrismCheckStepMm);
            if (step <= 0) step = Mm(250);
            int n = (int)Math.Ceiling(s.LenW / step);
            n = Math.Max(5, Math.Min(n, 500));
            double half = Math.Max(Mm(cfg.ProbeSliceMm), Mm(2)) * 0.5;

            // estaciones interiores equiespaciadas; nunca sobre las propias tapas
            var samples = new SectionSample[n];
            for (int i = 0; i < n; i++)
            {
                double w = s.LenW * (i + 0.5) / n;
                samples[i] = SampleSection(s, w, half);
                if (samples[i] == null)
                { why = "no hay hormigon (o no se pudo cortar el solido) en la estacion w=" + ToMm(w) + " mm"; return false; }
            }

            // referencia: la seccion central, que es la que despues se usa para armar
            SectionSample r = samples[n / 2];
            double areaTol = Math.Max(0.01 * r.Area, tol * 2 * (s.LenU + s.LenV));

            var bad = new List<string>();
            for (int i = 0; i < n; i++)
            {
                if (!SameProfile(r, samples[i], tol, areaTol, out string diff))
                    bad.Add("w=" + ToMm(samples[i].W) + " mm (" + diff + ")");
            }
            if (bad.Count == 0) return true;

            why = "la seccion central (w=" + ToMm(r.W) + " mm: " + r.Describe() + ") no se repite en " +
                  bad.Count + " de " + n + " estaciones, p.ej. " + string.Join("; ", bad.Take(3)) +
                  (bad.Count > 3 ? "; ..." : "");
            return false;
        }

        private static SectionSample SampleSection(WallSection s, double w, double half)
        {
            Solid slab = Intersect(s, -1, s.LenU + 1, -1, s.LenV + 1, w - half, w + half);
            if (slab == null) return null;

            var sm = new SectionSample { W = w };
            double ou = s.Origin.DotProduct(s.DirU), ov = s.Origin.DotProduct(s.DirV);
            UV Proj(XYZ p) => new UV(p.DotProduct(s.DirU) - ou, p.DotProduct(s.DirV) - ov);

            // area: tapas de la rebanada (caras con normal paralela al eje), sumadas por lado
            foreach (Face f in slab.Faces)
            {
                if (!(f is PlanarFace pf)) continue;
                double d = pf.FaceNormal.DotProduct(s.DirW);
                if (d > 0.999) sm.AreaB += pf.Area;
                else if (d < -0.999) sm.AreaA += pf.Area;
            }
            sm.Area = Math.Max(sm.AreaA, sm.AreaB);
            if (sm.Area <= 0) sm.Area = slab.Volume / (2 * half);

            // contorno: todas las aristas proyectadas al plano (u,v). Las aristas
            // paralelas al eje se proyectan en un punto y no cuentan como segmento.
            double tiny = Mm(0.5);
            foreach (Edge e in slab.Edges)
            {
                IList<XYZ> pts = e.Tessellate();
                for (int i = 0; i + 1 < pts.Count; i++)
                {
                    UV a = Proj(pts[i]), b = Proj(pts[i + 1]);
                    AddVertex(sm, a, tiny);
                    AddVertex(sm, b, tiny);
                    if (a.DistanceTo(b) > tiny) sm.Segments.Add((a, b));
                }
            }
            return sm;
        }

        private static void AddVertex(SectionSample sm, UV p, double tiny)
        {
            sm.U0 = Math.Min(sm.U0, p.U); sm.U1 = Math.Max(sm.U1, p.U);
            sm.V0 = Math.Min(sm.V0, p.V); sm.V1 = Math.Max(sm.V1, p.V);
            foreach (UV q in sm.Vertices)
                if (q.DistanceTo(p) <= tiny) return;
            sm.Vertices.Add(p);
        }

        /// <summary>
        /// Dos secciones son la misma si coinciden en extension, en area y si cada vertice
        /// de una cae sobre el contorno de la otra (y viceversa). Comparar vertice contra
        /// contorno, y no vertice contra vertice, evita falsos rechazos cuando la operacion
        /// booleana parte una arista en dos en una estacion y no en otra.
        /// </summary>
        private static bool SameProfile(SectionSample r, SectionSample s, double tol, double areaTol, out string diff)
        {
            diff = null;
            if (s.AreaA > 0 && s.AreaB > 0 && Math.Abs(s.AreaA - s.AreaB) > areaTol)
            { diff = "la seccion cambia dentro de la propia rebanada"; return false; }
            if (Math.Abs(r.U0 - s.U0) > tol || Math.Abs(r.U1 - s.U1) > tol ||
                Math.Abs(r.V0 - s.V0) > tol || Math.Abs(r.V1 - s.V1) > tol)
            { diff = "extension distinta: " + s.Describe(); return false; }
            if (Math.Abs(r.Area - s.Area) > areaTol)
            { diff = "area distinta: " + s.Describe(); return false; }
            if (r.Segments.Count == 0 || s.Segments.Count == 0)
            { diff = "contorno ilegible"; return false; }
            if (!VerticesOnBoundary(s.Vertices, r.Segments, tol) || !VerticesOnBoundary(r.Vertices, s.Segments, tol))
            { diff = "contorno distinto"; return false; }
            return true;
        }

        private static bool VerticesOnBoundary(List<UV> verts, List<(UV a, UV b)> segs, double tol)
        {
            foreach (UV p in verts)
            {
                bool on = false;
                foreach (var sg in segs)
                    if (DistToSegment(p, sg.a, sg.b) <= tol) { on = true; break; }
                if (!on) return false;
            }
            return true;
        }

        private static double DistToSegment(UV p, UV a, UV b)
        {
            double dx = b.U - a.U, dy = b.V - a.V;
            double len2 = dx * dx + dy * dy;
            double t = len2 < 1e-18 ? 0 : Math.Max(0, Math.Min(1, ((p.U - a.U) * dx + (p.V - a.V) * dy) / len2));
            double px = a.U + t * dx - p.U, py = a.V + t * dy - p.V;
            return Math.Sqrt(px * px + py * py);
        }

        /// <summary>
        /// Comprobacion exacta por caras: en un prisma recto a lo largo de DirW cada cara
        /// es (a) una tapa perpendicular al eje situada en w=0 o w=LenW, o (b) una cara
        /// paralela al eje (normal perpendicular a DirW). Cualquier otra cosa (tapa en
        /// el interior del recorrido, cara oblicua, cara curva no paralela) implica que
        /// la seccion cambia en algun punto.
        /// </summary>
        private static bool FacesAreParallelOrEndCaps(WallSection s, double tol, out string why)
        {
            why = null;
            const double ang = 1e-3;   // seno del angulo admisible, ~0.06 grados
            double w0 = s.Origin.DotProduct(s.DirW), w1 = w0 + s.LenW;

            foreach (Face f in s.HostSolid.Faces)
            {
                if (f is PlanarFace pf)
                {
                    double d = Math.Abs(pf.FaceNormal.DotProduct(s.DirW));
                    double sinToAxis = Math.Sqrt(Math.Max(0, 1 - d * d));
                    if (sinToAxis < ang)
                    {
                        // tapa: solo puede estar en los extremos del recorrido
                        double w = pf.Origin.DotProduct(s.DirW);
                        if (Math.Abs(w - w0) > tol && Math.Abs(w - w1) > tol)
                        {
                            why = "hay una cara perpendicular al eje en el interior del recorrido (w=" + ToMm(w - w0) +
                                  " mm): rincon de una L, contrafuerte o escalon";
                            return false;
                        }
                    }
                    else if (d > ang)
                    {
                        double deg = Math.Asin(Math.Min(1, d)) * 180 / Math.PI;
                        why = "hay una cara oblicua al eje (" + deg.ToString("0.0") +
                              " grados): extremo a inglete o eje mal detectado";
                        return false;
                    }
                }
                else if (!CurvedFaceIsParallel(f, s.DirW, ang))
                {
                    why = "hay una cara curva que no es paralela al eje";
                    return false;
                }
            }
            return true;
        }

        /// <summary>Cara no plana: su normal analitica debe ser perpendicular al eje en toda la cara.</summary>
        private static bool CurvedFaceIsParallel(Face f, XYZ dirW, double ang)
        {
            try
            {
                BoundingBoxUV bb = f.GetBoundingBox();
                const int g = 9;
                int tested = 0;
                for (int i = 0; i < g; i++)
                    for (int j = 0; j < g; j++)
                    {
                        var uv = new UV(bb.Min.U + (bb.Max.U - bb.Min.U) * (i + 0.5) / g,
                                        bb.Min.V + (bb.Max.V - bb.Min.V) * (j + 0.5) / g);
                        if (!f.IsInside(uv)) continue;
                        tested++;
                        if (Math.Abs(f.ComputeNormal(uv).DotProduct(dirW)) > ang) return false;
                    }
                // si no se pudo evaluar ningun punto, no se da por buena
                return tested > 0;
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------
        /// <summary>Altura maxima del solido dentro de una rebanada vertical en u.</summary>
        private static double? TopOfSliceAtU(WallSection s, double ua, double ub, double wa, double wb)
        {
            Solid r = Intersect(s, ua, ub, -1, s.LenV + 1, wa, wb);
            if (r == null) return null;
            var pts = Vertices(r);
            if (pts.Count == 0) return null;
            double vMax = pts.Max(p => p.DotProduct(s.DirV)) - s.Origin.DotProduct(s.DirV);
            return vMax;
        }

        /// <summary>Extension en u del solido a una altura v dada.</summary>
        private static Tuple<double, double> StemExtentsAt(WallSection s, double v, double slice, double wa, double wb)
        {
            Solid r = Intersect(s, -1, s.LenU + 1, v, v + slice, wa, wb);
            if (r == null) return null;
            var pts = Vertices(r);
            if (pts.Count == 0) return null;
            double o = s.Origin.DotProduct(s.DirU);
            return Tuple.Create(pts.Min(p => p.DotProduct(s.DirU)) - o,
                                pts.Max(p => p.DotProduct(s.DirU)) - o);
        }

        private static Solid Intersect(WallSection s, double ua, double ub, double va, double vb, double wa, double wb)
        {
            try
            {
                Solid box = Box(s, ua, ub, va, vb, wa, wb);
                Solid r = BooleanOperationsUtils.ExecuteBooleanOperation(s.HostSolid, box, BooleanOperationsType.Intersect);
                return (r != null && r.Volume > 1e-9) ? r : null;
            }
            catch { return null; }
        }

        private static Solid Box(WallSection s, double ua, double ub, double va, double vb, double wa, double wb)
        {
            XYZ p0 = s.P(ua, va, wa);
            XYZ p1 = s.P(ub, va, wa);
            XYZ p2 = s.P(ub, va, wb);
            XYZ p3 = s.P(ua, va, wb);

            var pts = new List<XYZ> { p0, p1, p2, p3 };
            // orientar el loop para que su normal apunte como DirV
            XYZ n = (pts[1] - pts[0]).CrossProduct(pts[2] - pts[1]);
            if (n.DotProduct(s.DirV) < 0) pts.Reverse();

            var loop = new CurveLoop();
            for (int i = 0; i < 4; i++)
                loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % 4]));

            return GeometryCreationUtilities.CreateExtrusionGeometry(
                new List<CurveLoop> { loop }, s.DirV, vb - va);
        }

        // ------------------------------------------------------------------
        /// <summary>
        /// Eje longitudinal del muro.
        /// IMPORTANTE: no se mide sobre el solido completo. En un muro corto la zapata
        /// es mas ancha que larga y la dimension mayor seria la transversal, no el eje.
        /// Se mide sobre la cara superior del alzado, donde solo hay alzado: un rectangulo
        /// de espesor de coronacion por longitud del muro. Ahi la dimension mayor
        /// siempre es el eje, mida el muro 1 m o 30 m.
        /// </summary>
        private static XYZ LongAxis(Element host, List<XYZ> pts, SectionOverride ov)
        {
            var cands = new List<XYZ>();

            if (host is Wall w && w.Location is LocationCurve lc && lc.Curve is Line ln)
                cands.Add(Flat(ln.Direction));

            if (host is FamilyInstance fi)
            {
                Transform t = fi.GetTransform();
                cands.Add(Flat(t.BasisX));
                cands.Add(Flat(t.BasisY));
            }
            cands.Add(XYZ.BasisX);
            cands.Add(XYZ.BasisY);
            cands = cands.Where(c => c != null).ToList();

            // forzado manual desde config.json
            if (!string.IsNullOrWhiteSpace(ov.AxisMode))
            {
                string m = ov.AxisMode.Trim().ToLowerInvariant();
                if (m == "x") return ov.FlipAxis ? XYZ.BasisX.Negate() : XYZ.BasisX;
                if (m == "y") return ov.FlipAxis ? XYZ.BasisY.Negate() : XYZ.BasisY;
            }

            // puntos de la cara superior del alzado
            double zMax = pts.Max(p => p.Z);
            double band = Mm(20);
            var top = pts.Where(p => p.Z > zMax - band).ToList();
            if (top.Count < 4) top = pts;   // seguridad: geometrias raras

            XYZ best = cands[0];
            double bestSpan = -1;
            foreach (XYZ c in cands)
            {
                double span = top.Max(p => p.DotProduct(c)) - top.Min(p => p.DotProduct(c));
                if (span > bestSpan) { bestSpan = span; best = c; }
            }
            return ov.FlipAxis ? best.Negate() : best;
        }

        private static XYZ Flat(XYZ v)
        {
            XYZ f = new XYZ(v.X, v.Y, 0);
            return f.GetLength() < 1e-6 ? null : f.Normalize();
        }

        /// <summary>Todos los solidos con volumen del elemento, de mayor a menor.</summary>
        public static List<Solid> Solids(Element e)
        {
            var list = new List<Solid>();
            var opt = new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false };
            GeometryElement ge = e.get_Geometry(opt);
            if (ge == null) return list;

            void Scan(IEnumerable<GeometryObject> objs)
            {
                foreach (GeometryObject go in objs)
                {
                    if (go is Solid sol) { if (sol.Volume > 1e-9) list.Add(sol); }
                    else if (go is GeometryInstance gi) Scan(gi.GetInstanceGeometry());
                }
            }
            Scan(ge);
            return list.OrderByDescending(x => x.Volume).ToList();
        }

        public static Solid LargestSolid(Element e) => Solids(e).FirstOrDefault();

        private static List<XYZ> Vertices(Solid s)
        {
            var pts = new List<XYZ>();
            foreach (Edge ed in s.Edges)
                foreach (XYZ p in ed.Tessellate()) pts.Add(p);
            return pts;
        }

        private static string TypeNameOf(Document doc, Element e)
        {
            ElementId tid = e.GetTypeId();
            Element t = (tid != null && tid != ElementId.InvalidElementId) ? doc.GetElement(tid) : null;
            return t?.Name ?? e.Name;
        }

        public static double Mm(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
    }
}
