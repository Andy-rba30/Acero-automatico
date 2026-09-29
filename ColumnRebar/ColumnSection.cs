using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace ColumnRebar
{
    /// <summary>
    /// Seccion de una columna deducida de la geometria real del elemento: se corta el solido
    /// con rebanadas horizontales finas, se lee el contorno de la rebanada central, se
    /// comprueba que es un poligono rectilineo (bordes paralelos a dos ejes) y que la
    /// seccion es constante en toda la altura (prisma vertical). Despues se descompone en
    /// rectangulos maximos, uno por estribo.
    ///
    /// Sistema local: origen en la esquina minima del contorno a la cota de la base,
    /// DirU horizontal a lo largo del borde mas largo, DirV = Z x DirU, y la cota absoluta z.
    /// Todo en pies (unidades internas de Revit).
    /// </summary>
    public sealed class ColumnSection
    {
        public const double MmPerFt = 304.8;
        public static double Mm(double mm) => mm / MmPerFt;
        public static double ToMm(double ft) => Math.Round(ft * MmPerFt);

        public Element Host;
        /// <summary>Solido contra el que se comprueban las barras (geometria completa de la familia si se recupero).</summary>
        public Solid HostSolid;
        /// <summary>Solido tal y como lo ve Revit (cortado por uniones), o el mismo que HostSolid.</summary>
        public Solid CutSolid;
        public bool UsedOriginal;
        public string JoinedNote;

        public XYZ Origin, DirU, DirV;
        public double ZBase, ZTop;
        public double Height => ZTop - ZBase;

        /// <summary>Contorno en coordenadas locales (u, v), antihorario, con la esquina minima en (0, 0).</summary>
        public List<Pt> Polygon;
        public List<Rect> Rects;
        public double Width, Depth;
        public string KindName;

        public static string LastError;

        public XYZ World(double u, double v, double z) =>
            new XYZ(Origin.X + DirU.X * u + DirV.X * v, Origin.Y + DirU.Y * u + DirV.Y * v, z);

        public Pt Local(XYZ p)
        {
            XYZ d = p - Origin;
            return new Pt(d.DotProduct(DirU), d.DotProduct(DirV));
        }

        public string LocalMm(XYZ p)
        {
            Pt l = Local(p);
            return "u=" + ToMm(l.U) + " v=" + ToMm(l.V) + " z=" + ToMm(p.Z - ZBase);
        }

        public string Describe()
        {
            string d = "seccion " + ToMm(Width) + " x " + ToMm(Depth) + " mm " + KindName + ", " +
                       Rects.Count + (Rects.Count == 1 ? " estribo" : " estribos") + ", altura " +
                       (Height * 0.3048).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " m";
            return d + (JoinedNote ?? "");
        }

        // ------------------------------------------------------------------
        // Deduccion
        // ------------------------------------------------------------------
        public static ColumnSection Probe(Document doc, Element host, AppConfig cfg)
        {
            LastError = null;
            var s = new ColumnSection { Host = host };

            List<Solid> cut = Solids(host);
            if (cut.Count == 0) { LastError = "el elemento no tiene geometria solida"; return null; }
            if (cut.Count > 1 && cut[1].Volume > 0.01 * cut[0].Volume)
            {
                LastError = "el elemento tiene " + cut.Count + " solidos; se esperaba uno solo (columna maciza)";
                return null;
            }
            s.CutSolid = cut[0];
            s.HostSolid = cut[0];

            // Geometria completa de la familia, por si otros elementos (vigas, losas) le han
            // restado hormigon con Unir geometria: la armadura de la columna sigue de largo.
            if (host is FamilyInstance fi)
            {
                Solid whole = OriginalSolid(fi, s.CutSolid);
                if (whole != null && whole.Volume > s.CutSolid.Volume * 1.001)
                {
                    s.HostSolid = whole;
                    s.UsedOriginal = true;
                    s.JoinedNote = " (unida a otros elementos: se arma con la geometria completa de la familia, " +
                                   Vol(whole) + " m3 frente a " + Vol(s.CutSolid) + " m3 visibles)";
                }
            }

            // --- extension vertical ---
            Bounds(s.HostSolid, out XYZ min, out XYZ max);
            s.ZBase = min.Z;
            s.ZTop = max.Z;
            double slice = Mm(cfg.ProbeSliceMm);
            if (s.Height < 10 * slice) { LastError = "la columna es demasiado baja (" + ToMm(s.Height) + " mm)"; return null; }

            // --- contorno de referencia a media altura ---
            double zMid = 0.5 * (s.ZBase + s.ZTop);
            List<XYZ> refWorld = Slice(s.HostSolid, zMid, slice, min, max, out string why);
            if (refWorld == null) { LastError = "no se pudo leer la seccion a media altura: " + why; return null; }

            // --- ejes locales: el borde mas largo ---
            double tol = Mm(cfg.PrismCheckToleranceMm);
            XYZ dirU = null;
            double best = 0;
            for (int i = 0; i < refWorld.Count; i++)
            {
                XYZ a = refWorld[i], b = refWorld[(i + 1) % refWorld.Count];
                XYZ d = new XYZ(b.X - a.X, b.Y - a.Y, 0);
                if (d.GetLength() > best) { best = d.GetLength(); dirU = d.Normalize(); }
            }
            if (dirU == null || best < tol) { LastError = "seccion degenerada"; return null; }
            s.DirU = dirU;
            s.DirV = XYZ.BasisZ.CrossProduct(dirU).Normalize();

            List<Pt> local = ToLocal(refWorld, s.DirU, s.DirV, tol);
            if (local.Count < 4) { LastError = "la seccion tiene menos de 4 vertices"; return null; }
            double angleTol = cfg.RectilinearAngleDeg * Math.PI / 180;
            if (!Rectilinear.IsRectilinear(local, angleTol, out string badEdge))
            {
                LastError = "la seccion no es rectilinea (" + badEdge + "): solo se admiten secciones con bordes " +
                            "paralelos a dos ejes (rectangular, L, T, U, cruz...)";
                return null;
            }
            local = Rectilinear.Snap(local, tol);
            local = Rectilinear.Simplify(local, tol);
            if (Rectilinear.SignedArea(local) < 0) local.Reverse();

            // --- prisma vertical: la misma seccion en todas las estaciones ---
            int stations = Math.Max(5, (int)Math.Ceiling(s.Height / Mm(cfg.PrismCheckStepMm)));
            double margin = Math.Max(slice, 0.02 * s.Height);
            for (int k = 0; k <= stations; k++)
            {
                double z = s.ZBase + margin + (s.Height - 2 * margin) * k / stations;
                List<XYZ> w = Slice(s.HostSolid, z, slice, min, max, out string why2);
                if (w == null)
                {
                    LastError = "no se pudo leer la seccion a " + ToMm(z - s.ZBase) + " mm de la base: " + why2;
                    return null;
                }
                List<Pt> lk = Rectilinear.Simplify(Rectilinear.Snap(ToLocal(w, s.DirU, s.DirV, tol), tol), tol);
                if (!SamePolygon(local, lk, tol))
                {
                    LastError = "no es un prisma vertical: la seccion a " + ToMm(z - s.ZBase) + " mm de la base es distinta " +
                                "de la de media altura (columna inclinada, con capitel, escalonada o con un vacio). " +
                                "Solo se arman columnas de seccion constante";
                    return null;
                }
            }

            // --- origen en la esquina minima ---
            double umin = local.Min(p => p.U), vmin = local.Min(p => p.V);
            s.Polygon = local.Select(p => new Pt(p.U - umin, p.V - vmin)).ToList();
            s.Origin = new XYZ(s.DirU.X * umin + s.DirV.X * vmin, s.DirU.Y * umin + s.DirV.Y * vmin, s.ZBase);
            s.Width = s.Polygon.Max(p => p.U);
            s.Depth = s.Polygon.Max(p => p.V);
            s.Rects = Rectilinear.MaximalRectangles(s.Polygon, tol);
            if (s.Rects.Count == 0) { LastError = "no se pudo descomponer la seccion en rectangulos"; return null; }
            s.KindName = Rectilinear.Kind(s.Polygon, s.Rects);
            return s;
        }

        private static string Vol(Solid s) => (s.Volume * 0.0283168).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

        private static List<Pt> ToLocal(List<XYZ> world, XYZ dirU, XYZ dirV, double tol)
        {
            var pts = world.Select(p => new Pt(p.X * dirU.X + p.Y * dirU.Y, p.X * dirV.X + p.Y * dirV.Y)).ToList();
            return Rectilinear.Simplify(pts, tol);
        }

        /// <summary>Mismos vertices (sin importar el orden ni el punto de arranque) con tolerancia.</summary>
        private static bool SamePolygon(List<Pt> a, List<Pt> b, double tol)
        {
            if (a.Count != b.Count) return false;
            if (Math.Abs(Math.Abs(Rectilinear.SignedArea(a)) - Math.Abs(Rectilinear.SignedArea(b))) > tol * (a.Max(p => p.U) - a.Min(p => p.U) + a.Max(p => p.V) - a.Min(p => p.V)) * 2 + 1e-9) return false;
            foreach (Pt p in a)
                if (!b.Any(q => q.DistanceTo(p) <= 2 * tol)) return false;
            return true;
        }

        // ------------------------------------------------------------------
        // Rebanadas
        // ------------------------------------------------------------------

        /// <summary>
        /// Contorno (en coordenadas del modelo) de la seccion horizontal del solido a la cota
        /// z: se interseca con una caja fina y se lee la cara superior de la rebanada. Null
        /// con el motivo si la seccion esta partida, es hueca o tiene bordes curvos.
        /// </summary>
        public static List<XYZ> Slice(Solid solid, double z, double thickness, XYZ min, XYZ max, out string why)
        {
            why = null;
            Solid box = Box(min.X - 1, min.Y - 1, max.X + 1, max.Y + 1, z - 0.5 * thickness, thickness);
            Solid piece;
            try { piece = BooleanOperationsUtils.ExecuteBooleanOperation(solid, box, BooleanOperationsType.Intersect); }
            catch (Exception ex) { why = "fallo la operacion booleana (" + ex.Message + ")"; return null; }
            if (piece == null || piece.Volume < 1e-9) { why = "no hay hormigon a esa cota"; return null; }

            var tops = new List<PlanarFace>();
            foreach (Face f in piece.Faces)
                if (f is PlanarFace pf && pf.FaceNormal.Z > 0.99 && Math.Abs(pf.Origin.Z - (z + 0.5 * thickness)) < 0.5 * thickness)
                    tops.Add(pf);
            if (tops.Count == 0) { why = "la rebanada no tiene cara superior plana"; return null; }
            if (tops.Count > 1) { why = "la seccion esta partida en " + tops.Count + " trozos"; return null; }

            IList<CurveLoop> loops = tops[0].GetEdgesAsCurveLoops();
            if (loops.Count != 1) { why = "la seccion es hueca (" + loops.Count + " contornos)"; return null; }

            var pts = new List<XYZ>();
            foreach (Curve c in loops[0])
            {
                if (!(c is Line))
                {
                    why = "tiene bordes curvos (columna circular o con esquinas redondeadas); solo se admiten secciones poligonales";
                    return null;
                }
                pts.Add(c.GetEndPoint(0));
            }
            return pts;
        }

        private static Solid Box(double x1, double y1, double x2, double y2, double z0, double h)
        {
            var loop = new CurveLoop();
            var p1 = new XYZ(x1, y1, z0); var p2 = new XYZ(x2, y1, z0);
            var p3 = new XYZ(x2, y2, z0); var p4 = new XYZ(x1, y2, z0);
            loop.Append(Line.CreateBound(p1, p2));
            loop.Append(Line.CreateBound(p2, p3));
            loop.Append(Line.CreateBound(p3, p4));
            loop.Append(Line.CreateBound(p4, p1));
            return GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, XYZ.BasisZ, h);
        }

        // ------------------------------------------------------------------
        // Solidos
        // ------------------------------------------------------------------
        private static Options GeometryOptions() =>
            new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false, IncludeNonVisibleObjects = false };

        /// <summary>Todos los solidos con volumen del elemento, de mayor a menor.</summary>
        public static List<Solid> Solids(Element e) => ScanSolids(e.get_Geometry(GeometryOptions()));

        private static List<Solid> ScanSolids(GeometryElement ge)
        {
            var list = new List<Solid>();
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

        /// <summary>
        /// Solido de la familia antes de uniones y cortes (GetOriginalGeometry). La API no
        /// garantiza el sistema de coordenadas en que lo devuelve: se prueba tal cual y
        /// transformado por la instancia, y se elige el que contiene al solido cortado.
        /// Null si no hay nada mejor que el solido cortado.
        /// </summary>
        private static Solid OriginalSolid(FamilyInstance fi, Solid cut)
        {
            List<Solid> raw;
            try { raw = ScanSolids(fi.GetOriginalGeometry(GeometryOptions())); }
            catch { return null; }
            if (raw.Count == 0) return null;
            var candidates = new List<Solid> { raw[0] };
            try
            {
                Transform t = fi.GetTransform();
                if (t != null && !t.IsIdentity) candidates.Add(SolidUtils.CreateTransformed(raw[0], t));
            }
            catch { }
            foreach (Solid c in candidates)
            {
                if (c.Volume < cut.Volume * 0.999) continue;
                try
                {
                    Solid common = BooleanOperationsUtils.ExecuteBooleanOperation(c, cut, BooleanOperationsType.Intersect);
                    if (common != null && common.Volume >= 0.98 * cut.Volume) return c;
                }
                catch { }
            }
            return null;
        }

        internal static void Bounds(Solid s, out XYZ min, out XYZ max)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue;
            double x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
            foreach (Edge ed in s.Edges)
                foreach (XYZ p in ed.Tessellate())
                {
                    x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); z0 = Math.Min(z0, p.Z);
                    x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); z1 = Math.Max(z1, p.Z);
                }
            min = new XYZ(x0, y0, z0);
            max = new XYZ(x1, y1, z1);
        }

        internal static string TypeNameOf(Document doc, Element e)
        {
            ElementId tid = e.GetTypeId();
            Element t = (tid != null && tid != ElementId.InvalidElementId) ? doc.GetElement(tid) : null;
            return t?.Name ?? e.Name;
        }
    }
}
