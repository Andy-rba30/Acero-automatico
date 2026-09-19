using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace RetainingWallRebar
{
    /// <summary>Un conjunto (elemento Rebar) creado para un anfitrion.</summary>
    public sealed class CreatedSet
    {
        public ElementId Id;
        public string Name;
        /// <summary>Radio nominal de la barra (pies).</summary>
        public double Radius;
    }

    /// <summary>Resultado del armado de un elemento.</summary>
    public sealed class BuildResult
    {
        public List<CreatedSet> Created = new List<CreatedSet>();

        /// <summary>
        /// Barras que quedarian (total o parcialmente) fuera del hormigon del anfitrion.
        /// Si hay alguna, el elemento entero se deshace: nunca se deja un muro armado a
        /// medias ni con barras en el aire.
        /// </summary>
        public List<string> Rejected = new List<string>();

        /// <summary>Familias que Revit no pudo crear (geometria degenerada, etc.).</summary>
        public List<string> Failed = new List<string>();

        public bool Safe => Rejected.Count == 0;
    }

    public static class RebarGenerator
    {
        private static double Mm(double mm) => WallSection.Mm(mm);
        private static double ToMm(double ft) => WallSection.ToMm(ft);
        private const double MinSeg = 0.003; // ~1 mm en pies

        /// <summary>Longitud de barra que se tolera fuera del solido al comprobar (pies, ~1 mm).</summary>
        private const double InsideTol = 0.0033;

        private enum Layout { Single, ArrayIfLonger, Array }

        private sealed class Ctx
        {
            public Document Doc;
            public Element Host;
            public WallSection S;
            public AppConfig Cfg;
            public string Partition;
            public BuildResult Result;

            /// <summary>Tras el primer rechazo solo se comprueba, no se crea: el elemento se va a deshacer entero.</summary>
            public bool DryRun => Result.Rejected.Count > 0;
        }

        public static BuildResult Build(Document doc, Element host, WallSection s, AppConfig cfg)
        {
            var c = new Ctx
            {
                Doc = doc, Host = host, S = s, Cfg = cfg,
                Partition = PartitionName(doc, host),
                Result = new BuildResult()
            };

            StemVerticals(c, back: true);
            StemVerticals(c, back: false);
            FootingTransverse(c, top: false);
            FootingTransverse(c, top: true);
            FootingLongitudinal(c, top: false);
            FootingLongitudinal(c, top: true);
            StemHorizontals(c, back: true);
            StemHorizontals(c, back: false);
            return c.Result;
        }

        // =================================================================
        // Verticales del alzado: tramo inclinado siguiendo la cara + patilla en zapata
        // =================================================================
        private static void StemVerticals(Ctx c, bool back)
        {
            WallSection s = c.S;
            AppConfig cfg = c.Cfg;
            BarFamilyCfg f = back ? cfg.StemVerticalBack : cfg.StemVerticalFront;
            if (!f.Enabled) return;
            string name = "vertical " + (back ? "trasdos" : "intrados");

            RebarBarType bt = FindBarType(c.Doc, f.BarTypeName);
            double db = bt.BarNominalDiameter;
            double cov = Mm(cfg.CoverStemMm);

            // lado de trabajo: u0 = cara u minima, u1 = cara u maxima
            bool useU0 = back ? s.HeelAtU0 : !s.HeelAtU0;

            Func<double, double> face = useU0 ? (Func<double, double>)s.FaceU0 : s.FaceU1;
            double sign = useU0 ? +1 : -1;               // hacia el interior del alzado
            double uAt(double v) => face(v) + sign * (cov + db * 0.5);

            double vTop = s.LenV - Mm(cfg.CoverStemTopMm) - db * 0.5;
            double vBot = Mm(cfg.CoverFootingBottomMm) + db * 0.5;
            if (f.CutLengthMm > 0) vTop = Math.Min(vTop, s.FootingTop + Mm(f.CutLengthMm));

            // La barra de definicion va en w = recubrimiento de extremo y el array avanza
            // desde ahi hasta LenW - recubrimiento. (Antes se definia en w = 0, es decir,
            // con el eje de la primera barra sobre la propia cara de testa.)
            double wa = Mm(cfg.CoverEndMm);
            XYZ pTop = s.P(uAt(vTop), vTop, wa);
            XYZ pBend = s.P(uAt(vBot), vBot, wa);

            // patilla horizontal hacia el interior de la zapata, acotada por el canto util
            double leg = Mm(f.LegMm);
            double uBend = uAt(vBot);
            double uEnd = uBend - sign * leg;           // cruza hacia el lado opuesto
            double uLim0 = Mm(cfg.CoverFootingSideMm) + db;
            double uLim1 = s.LenU - Mm(cfg.CoverFootingSideMm) - db;
            uEnd = Math.Min(Math.Max(uEnd, uLim0), uLim1);
            XYZ pLeg = s.P(uEnd, vBot, wa);

            var curves = new List<Curve>();
            AddLine(curves, pTop, pBend);
            AddLine(curves, pBend, pLeg);
            if (curves.Count == 0) { c.Result.Failed.Add(name + ": geometria degenerada"); return; }

            Place(c, name, bt, s.DirW, curves, Mm(f.SpacingMm), s.LenW - 2 * wa, Layout.ArrayIfLonger, true);
        }

        // =================================================================
        // Armado transversal de zapata (superior e inferior) con patas en extremos
        // =================================================================
        private static void FootingTransverse(Ctx c, bool top)
        {
            WallSection s = c.S;
            AppConfig cfg = c.Cfg;
            BarFamilyCfg f = top ? cfg.FootingTransverseTop : cfg.FootingTransverseBottom;
            if (!f.Enabled) return;
            string name = "transversal zapata " + (top ? "superior" : "inferior");

            RebarBarType bt = FindBarType(c.Doc, f.BarTypeName);
            double db = bt.BarNominalDiameter;

            double v = top
                ? s.FootingTop - Mm(cfg.CoverFootingTopMm) - db * 0.5
                : Mm(cfg.CoverFootingBottomMm) + db * 0.5;

            double ua = Mm(cfg.CoverFootingSideMm) + db * 0.5;
            double ub = s.LenU - ua;
            double leg = Mm(f.LegMm);
            double legSign = top ? -1 : +1;   // superior baja, inferior sube
            double vLeg = v + legSign * leg;
            vLeg = Math.Min(Math.Max(vLeg, Mm(cfg.CoverFootingBottomMm)), s.FootingTop - Mm(cfg.CoverFootingTopMm));

            double wa = Mm(cfg.CoverEndMm);   // barra de definicion en w = recubrimiento (antes w = 0)
            var curves = new List<Curve>();
            if (leg > MinSeg) AddLine(curves, s.P(ua, vLeg, wa), s.P(ua, v, wa));
            AddLine(curves, s.P(ua, v, wa), s.P(ub, v, wa));
            if (leg > MinSeg) AddLine(curves, s.P(ub, v, wa), s.P(ub, vLeg, wa));
            if (curves.Count == 0) { c.Result.Failed.Add(name + ": geometria degenerada"); return; }

            Place(c, name, bt, s.DirW, curves, Mm(f.SpacingMm), s.LenW - 2 * wa, Layout.ArrayIfLonger, true);
        }

        // =================================================================
        // Reparto longitudinal de zapata: una sola barra definida + array en U
        // =================================================================
        private static void FootingLongitudinal(Ctx c, bool top)
        {
            WallSection s = c.S;
            AppConfig cfg = c.Cfg;
            BarFamilyCfg f = top ? cfg.FootingLongitudinalTop : cfg.FootingLongitudinalBottom;
            if (!f.Enabled) return;
            string name = "longitudinal zapata " + (top ? "superior" : "inferior");

            RebarBarType bt = FindBarType(c.Doc, f.BarTypeName);
            double db = bt.BarNominalDiameter;
            RebarBarType btTrans = FindBarType(c.Doc, (top ? cfg.FootingTransverseTop : cfg.FootingTransverseBottom).BarTypeName);

            // se apoya por dentro del transversal
            double v = top
                ? s.FootingTop - Mm(cfg.CoverFootingTopMm) - btTrans.BarNominalDiameter - db * 0.5
                : Mm(cfg.CoverFootingBottomMm) + btTrans.BarNominalDiameter + db * 0.5;

            double u0 = Mm(cfg.CoverFootingSideMm) + db * 0.5;
            double span = s.LenU - 2 * u0;
            if (span <= MinSeg) { c.Result.Failed.Add(name + ": no cabe en el ancho de zapata"); return; }

            double wa = Mm(cfg.CoverEndMm);
            double wb = s.LenW - wa;
            var curves = new List<Curve> { Line.CreateBound(s.P(u0, v, wa), s.P(u0, v, wb)) };

            Place(c, name, bt, s.DirU, curves, Mm(f.SpacingMm), span, Layout.Array, true);
        }

        // =================================================================
        // Reparto horizontal del alzado. El alzado va en talud, asi que o bien
        // se coloca barra a barra (exacto) o por bandas (menos elementos).
        // =================================================================
        private static void StemHorizontals(Ctx c, bool back)
        {
            WallSection s = c.S;
            AppConfig cfg = c.Cfg;
            BarFamilyCfg f = back ? cfg.StemHorizontalBack : cfg.StemHorizontalFront;
            if (!f.Enabled) return;
            string name = "horizontal alzado " + (back ? "trasdos" : "intrados");

            RebarBarType bt = FindBarType(c.Doc, f.BarTypeName);
            double db = bt.BarNominalDiameter;
            RebarBarType btVert = FindBarType(c.Doc, (back ? cfg.StemVerticalBack : cfg.StemVerticalFront).BarTypeName);
            double cov = Mm(cfg.CoverStemMm) + btVert.BarNominalDiameter;

            bool useU0 = back ? s.HeelAtU0 : !s.HeelAtU0;
            Func<double, double> face = useU0 ? (Func<double, double>)s.FaceU0 : s.FaceU1;
            double sign = useU0 ? +1 : -1;

            double spacing = Mm(f.SpacingMm);
            double vStart = s.FootingTop + spacing * 0.5;
            double vEnd = s.LenV - Mm(cfg.CoverStemTopMm) - db;
            if (vEnd <= vStart) return;

            double wa = Mm(cfg.CoverEndMm);
            double wb = s.LenW - wa;
            double band = Mm(cfg.StemHorizontalBandMm);

            if (band <= MinSeg)
            {
                for (double v = vStart; v <= vEnd; v += spacing)
                {
                    double u = face(v) + sign * (cov + db * 0.5);
                    var cl = new List<Curve> { Line.CreateBound(s.P(u, v, wa), s.P(u, v, wb)) };
                    Place(c, name + " v=" + ToMm(v) + " mm", bt, s.DirV, cl, 0, 0, Layout.Single, true);
                }
            }
            else
            {
                for (double v = vStart; v <= vEnd; v += band)
                {
                    double vHi = Math.Min(v + band, vEnd);
                    double vMid = (v + vHi) * 0.5;
                    double u = face(vMid) + sign * (cov + db * 0.5);
                    var cl = new List<Curve> { Line.CreateBound(s.P(u, v, wa), s.P(u, v, wb)) };
                    Place(c, name + " banda v=" + ToMm(v) + " mm", bt, s.DirV, cl, spacing, vHi - v, Layout.Array, false);
                }
            }
        }

        // =================================================================
        // Colocacion con red de seguridad
        // =================================================================

        /// <summary>
        /// Comprueba que la barra (y todas las posiciones del array) queda dentro del
        /// hormigon del anfitrion, y solo entonces la crea. Si no, la registra como
        /// rechazada y no crea nada.
        /// </summary>
        private static void Place(Ctx c, string name, RebarBarType bt, XYZ normal, List<Curve> curves,
                                  double spacing, double length, Layout layout, bool includeLast)
        {
            normal = normal.Normalize();
            bool array = layout == Layout.Array || (layout == Layout.ArrayIfLonger && length > spacing);
            if (array && (spacing <= 0 || length <= MinSeg)) array = false;

            // --- RED DE SEGURIDAD (1): geometria planificada, antes de crear nada ---
            // Se comprueba la barra de definicion y cada posicion del array, con la
            // misma regla que aplica Revit a "separacion maxima": n = ceil(L/s) + 1
            // barras equiespaciadas con la primera y la ultima incluidas.
            double r = bt.BarNominalDiameter * 0.5;
            var offsets = new List<double> { 0 };
            if (array)
            {
                int n = (int)Math.Ceiling(length / spacing - 1e-9) + 1;
                double step = length / (n - 1);
                for (int k = 1; k < n; k++) offsets.Add(k * step);
            }
            foreach (double off in offsets)
            {
                IList<Curve> moved = curves;
                if (off > 0)
                {
                    Transform t = Transform.CreateTranslation(normal * off);
                    moved = curves.Select(cv => cv.CreateTransformed(t)).ToList();
                }
                if (!BarInside(c.S, moved, r, out string why))
                {
                    c.Result.Rejected.Add(name + " (posicion " + ToMm(off) + " mm del array): " + why);
                    return;
                }
            }
            if (c.DryRun) return;

            Rebar rb = Create(c.Doc, c.Host, bt, normal, curves, out string err);
            if (rb == null) { c.Result.Failed.Add(name + ": Revit no pudo crear la barra (" + err + ")"); return; }

            if (array)
                rb.GetShapeDrivenAccessor().SetLayoutAsMaximumSpacing(spacing, length, true, true, includeLast);
            else
                rb.GetShapeDrivenAccessor().SetLayoutAsSingle();

            Finish(c.Doc, rb, c.Partition);
            c.Result.Created.Add(new CreatedSet { Id = rb.Id, Name = name, Radius = r });
        }

        /// <summary>
        /// RED DE SEGURIDAD (2): tras crear y regenerar, se lee la geometria REAL de cada
        /// barra de cada conjunto tal y como la ha colocado Revit (incluidos los radios de
        /// doblado y todas las posiciones del array) y se comprueba contra el solido.
        /// Cualquier fallo se anota en Rejected; el comando deshace el elemento entero.
        /// </summary>
        public static void VerifyCreated(Document doc, WallSection s, BuildResult res)
        {
            foreach (CreatedSet cs in res.Created)
            {
                var rb = doc.GetElement(cs.Id) as Rebar;
                if (rb == null)
                { res.Rejected.Add(cs.Name + ": el conjunto no existe tras regenerar"); continue; }

                int n;
                try { n = rb.NumberOfBarPositions; }
                catch (Exception ex)
                { res.Rejected.Add(cs.Name + ": no se pudo leer el conjunto (" + ex.Message + ")"); continue; }

                for (int k = 0; k < n; k++)
                {
                    IList<Curve> cl;
                    try
                    {
                        if (!rb.DoesBarExistAtPosition(k)) continue;
                        cl = rb.GetCenterlineCurves(false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, k);
                    }
                    catch (Exception ex)
                    {
                        res.Rejected.Add(cs.Name + " (barra " + (k + 1) + " de " + n + "): no se pudo leer su geometria (" + ex.Message + ")");
                        break;
                    }
                    if (cl == null || cl.Count == 0)
                    { res.Rejected.Add(cs.Name + " (barra " + (k + 1) + " de " + n + "): sin geometria"); break; }

                    if (!BarInside(s, cl, cs.Radius, out string why))
                    { res.Rejected.Add(cs.Name + " (barra " + (k + 1) + " de " + n + "): " + why); break; }
                }
            }
        }

        /// <summary>
        /// True si toda la barra queda dentro del solido del anfitrion. Ademas del eje se
        /// comprueban seis fibras extremas (eje desplazado +-r en cada direccion local),
        /// asi una barra tangente a una cara, o con medio diametro fuera, tambien falla.
        /// No es la envolvente exacta en los codos, pero cualquier barra en el hueco de
        /// una L o asomando por una cara cae aqui.
        /// </summary>
        private static bool BarInside(WallSection s, IList<Curve> curves, double r, out string why)
        {
            why = null;
            var shifts = new List<XYZ>
            {
                XYZ.Zero,
                s.DirU * r, s.DirU * -r,
                s.DirV * r, s.DirV * -r,
                s.DirW * r, s.DirW * -r
            };

            foreach (Curve cv in curves)
            {
                foreach (XYZ sh in shifts)
                {
                    Curve probe = sh.IsZeroLength() ? cv : cv.CreateTransformed(Transform.CreateTranslation(sh));
                    if (!CurveInside(s.HostSolid, probe, out double outside))
                    {
                        why = "queda fuera del hormigon (" + ToMm(outside) + " mm de barra fuera; segmento de " +
                              s.LocalMm(cv.GetEndPoint(0)) + " a " + s.LocalMm(cv.GetEndPoint(1)) + ")";
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>Longitud de la curva que queda fuera del solido; no verificable cuenta como fuera.</summary>
        private static bool CurveInside(Solid solid, Curve cv, out double outsideLen)
        {
            outsideLen = cv.Length;
            try
            {
                var opt = new SolidCurveIntersectionOptions { ResultType = SolidCurveIntersectionMode.CurveSegmentsInside };
                SolidCurveIntersection ix = solid.IntersectWithCurve(cv, opt);
                double inside = 0;
                if (ix != null)
                    for (int i = 0; i < ix.SegmentCount; i++) inside += ix.GetCurveSegment(i).Length;
                outsideLen = Math.Max(0, cv.Length - inside);
                return outsideLen <= InsideTol;
            }
            catch { return false; }
        }

        // =================================================================
        // Utilidades
        // =================================================================
        private static void AddLine(List<Curve> list, XYZ a, XYZ b)
        {
            if (a.DistanceTo(b) > MinSeg) list.Add(Line.CreateBound(a, b));
        }

        private static Rebar Create(Document doc, Element host, RebarBarType bt, XYZ normal, IList<Curve> curves, out string err)
        {
            err = null;
            try
            {
                // Revit 2027: ganchos, acodados y tratamientos de extremo van agrupados
                // en BarTerminationsData. Los valores por defecto son "sin gancho", que es
                // lo que queremos: nuestras patillas son segmentos explicitos de la polilinea.
                using (BarTerminationsData term = new BarTerminationsData(doc))
                {
                    term.TerminationOrientationAtStart = RebarTerminationOrientation.Right;
                    term.TerminationOrientationAtEnd = RebarTerminationOrientation.Left;

                    return Rebar.CreateFromCurves(doc, RebarStyle.Standard, bt, host,
                        normal.Normalize(), curves, term, true, true);
                }
            }
            catch (Exception ex)
            {
                err = ex.Message;
                return null;
            }
        }

        private static void Finish(Document doc, Rebar r, string partition)
        {
            Parameter p = r.LookupParameter("Partition");
            if (p != null && !p.IsReadOnly && !string.IsNullOrEmpty(partition)) p.Set(partition);

            // Rebar.SetSolidInView se elimino de la API (obsoleto en 2023, ausente desde 2024).
            // La armadura ya se muestra solida en vistas 3D con nivel de detalle Fino;
            // si hiciera falta, se sobreescribe el nivel de detalle de la categoria
            // Structural Rebar en la vista.
            try { r.SetUnobscuredInView(doc.ActiveView, true); } catch { }
        }

        public static RebarBarType FindBarType(Document doc, string name)
        {
            var all = new FilteredElementCollector(doc)
                .OfClass(typeof(RebarBarType)).Cast<RebarBarType>().ToList();
            if (all.Count == 0)
                throw new InvalidOperationException(
                    "El proyecto no tiene ningun tipo de barra (RebarBarType). Carga una familia de armadura primero.");

            if (!string.IsNullOrWhiteSpace(name))
            {
                var exact = all.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
                if (exact != null) return exact;
                var partial = all.FirstOrDefault(b => b.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
                if (partial != null) return partial;
            }
            return all[0];
        }

        private static string PartitionName(Document doc, Element host)
        {
            Parameter mark = host.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
            string m = mark?.AsString();
            return string.IsNullOrWhiteSpace(m) ? "MURO " + host.Id.ToString() : m;
        }
    }
}
