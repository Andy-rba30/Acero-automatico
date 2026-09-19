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

        private static double Round(double ft) =>
            Math.Round(UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters));

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

            Solid solid = LargestSolid(host);
            if (solid == null || solid.Volume < 1e-9)
            { LastError = "no se encontro solido en el elemento"; return null; }

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

        public static Solid LargestSolid(Element e)
        {
            var opt = new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false };
            GeometryElement ge = e.get_Geometry(opt);
            if (ge == null) return null;

            Solid best = null;
            double bv = 0;
            void Scan(IEnumerable<GeometryObject> objs)
            {
                foreach (GeometryObject go in objs)
                {
                    if (go is Solid sol && sol.Volume > bv) { best = sol; bv = sol.Volume; }
                    else if (go is GeometryInstance gi) Scan(gi.GetInstanceGeometry());
                }
            }
            Scan(ge);
            return best;
        }

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
