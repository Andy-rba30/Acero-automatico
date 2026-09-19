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
            public WingPlan Plan;
            public AppConfig Cfg;
            /// <summary>Particion de un conjunto a partir de su nombre (plantilla de la configuracion).</summary>
            public Func<string, string> Partition;
            public BuildResult Result;

            /// <summary>Tras el primer rechazo solo se comprueba, no se crea: el elemento se va a deshacer entero.</summary>
            public bool DryRun => Result.Rejected.Count > 0;
        }

        /// <summary>Arma un tramo recto completo.</summary>
        public static BuildResult Build(Document doc, HostAnalysis item, AppConfig cfg)
        {
            WallSection s = item.Straight;
            return Build(doc, item, s, WingPlan.Straight(s, cfg), cfg, new BuildResult());
        }

        /// <summary>Arma un tramo (recto o ala de un esquinero) segun su plan, acumulando en result.</summary>
        public static BuildResult Build(Document doc, HostAnalysis item, WallSection s, WingPlan plan, AppConfig cfg, BuildResult result)
        {
            var c = new Ctx
            {
                Doc = doc, Host = item.Host, S = s, Plan = plan, Cfg = cfg,
                Partition = setName => item.Partition(cfg, s.Label, setName),
                Result = result
            };

            StemVerticals(c, back: true);
            StemVerticals(c, back: false);
            StemVerticals(c, back: true, dowel: true);
            StemVerticals(c, back: false, dowel: true);
            FootingTransverse(c, top: false);
            FootingTransverse(c, top: true);
            FootingReinforcements(c);
            FootingLongitudinal(c, top: false);
            FootingLongitudinal(c, top: true);
            StemHorizontals(c, back: true);
            StemHorizontals(c, back: false);
            return c.Result;
        }

        /// <summary>Arma las dos alas de un muro esquinero en L con sus reglas de esquina.</summary>
        public static BuildResult BuildCorner(Document doc, HostAnalysis item, AppConfig cfg)
        {
            CornerWall cw = item.Corner;
            WingPlan[] plans = cw.BuildPlans(doc, cfg);
            var result = new BuildResult();
            for (int k = 0; k < 2; k++)
                Build(doc, item, cw.Wings[k], plans[k], cfg, result);
            return result;
        }

        private static Func<string, double> Dia(Ctx c) => n => FindBarType(c.Doc, n).BarNominalDiameter;

        private static List<Curve> Curves(WallSection s, SectionBars.Poly p, double w)
        {
            var curves = new List<Curve>();
            for (int i = 0; i + 1 < p.Pts.Count; i++)
                AddLine(curves, s.P(p.Pts[i].u, p.Pts[i].v, w), s.P(p.Pts[i + 1].u, p.Pts[i + 1].v, w));
            return curves;
        }

        // =================================================================
        // Verticales del alzado: tramo inclinado siguiendo la cara + patilla que cruza
        // bajo la pantalla (geometria en SectionBars.Vertical, compartida con el esquema)
        // =================================================================
        private static void StemVerticals(Ctx c, bool back, bool dowel = false)
        {
            WallSection s = c.S;
            AppConfig cfg = c.Cfg;
            BarFamilyCfg f = dowel ? (back ? cfg.StemDowelBack : cfg.StemDowelFront)
                                   : (back ? cfg.StemVerticalBack : cfg.StemVerticalFront);
            if (!f.Enabled) return;
            string name = c.Plan.Label + (dowel ? "baston " : "vertical ") + (back ? "trasdos" : "intrados");

            double wa = c.Plan.VertW0, wb = c.Plan.VertW1;
            if (dowel)
            {
                // intercalado media separacion con las verticales enteras de su cara
                BarFamilyCfg main = back ? cfg.StemVerticalBack : cfg.StemVerticalFront;
                wa += Mm(main.Enabled ? main.SpacingMm : f.SpacingMm) * 0.5;
            }
            if (wb < wa) { c.Result.Failed.Add(name + ": no cabe en el tramo"); return; }

            RebarBarType bt = FindBarType(c.Doc, f.BarTypeName);
            SectionBars.Poly p = dowel ? SectionBars.Dowel(s, cfg, back, Dia(c), out _) : SectionBars.Vertical(s, cfg, back, Dia(c));
            if (p == null) { c.Result.Failed.Add(name + ": altura no valida"); return; }

            // La barra de definicion va en w = inicio del tramo (recubrimiento de extremo) y
            // el array avanza desde ahi hasta el final que marca el plan.
            List<Curve> curves = Curves(s, p, wa);
            if (curves.Count == 0) { c.Result.Failed.Add(name + ": geometria degenerada"); return; }

            Place(c, name, bt, s.DirW, curves, Mm(f.SpacingMm), wb - wa, Layout.ArrayIfLonger, true);
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
            string name = c.Plan.Label + "transversal zapata " + (top ? "superior" : "inferior");

            double wa = c.Plan.TransW0, wb = c.Plan.TransW1;
            if (wb < wa) { c.Result.Failed.Add(name + ": no cabe en el tramo"); return; }

            RebarBarType bt = FindBarType(c.Doc, f.BarTypeName);
            double otherDb = top ? c.Plan.OtherTransverseTopDb : c.Plan.OtherTransverseBottomDb;
            SectionBars.Poly p = SectionBars.Transverse(s, cfg, top, Dia(c), c.Plan.SwapFootingLayers, otherDb);

            List<Curve> curves = Curves(s, p, wa);
            if (curves.Count == 0) { c.Result.Failed.Add(name + ": geometria degenerada"); return; }

            Place(c, name, bt, s.DirW, curves, Mm(f.SpacingMm), wb - wa, Layout.ArrayIfLonger, true);
        }

        // =================================================================
        // Refuerzos transversales cortos de zapata: barra recta apilada sobre su
        // transversal (encima o debajo) y alineada con ella a lo largo del muro
        // =================================================================
        private static void FootingReinforcements(Ctx c)
        {
            WallSection s = c.S;
            AppConfig cfg = c.Cfg;
            if (cfg.FootingReinforcements == null) return;
            int i = 0;
            foreach (FootingReinfCfg r in cfg.FootingReinforcements)
            {
                i++;
                string name = c.Plan.Label + "refuerzo zapata " + r.Describe + " #" + i;
                double wa0 = c.Plan.TransW0, wb = c.Plan.TransW1;
                if (wb < wa0) { c.Result.Failed.Add(name + ": no cabe en el tramo"); continue; }

                RebarBarType bt = FindBarType(c.Doc, r.BarTypeName);
                double otherDb = r.Top ? c.Plan.OtherTransverseTopDb : c.Plan.OtherTransverseBottomDb;
                SectionBars.Poly p = SectionBars.Reinforcement(s, cfg, r, Dia(c), out string warn, out _, c.Plan.SwapFootingLayers, otherDb);
                if (p == null) { c.Result.Failed.Add(name + ": " + warn); continue; }

                // alineado con la transversal de su capa (apilado sobre ella)
                List<Curve> curves = Curves(s, p, wa0);
                if (curves.Count == 0) { c.Result.Failed.Add(name + ": geometria degenerada"); continue; }
                Place(c, name, bt, s.DirW, curves, Mm(r.SpacingMm), wb - wa0, Layout.ArrayIfLonger, true);
            }
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
            string name = c.Plan.Label + "longitudinal zapata " + (top ? "superior" : "inferior");

            double wa = c.Plan.LongW0, wb = c.Plan.LongW1;
            if (wb - wa <= MinSeg) { c.Result.Failed.Add(name + ": no cabe en el tramo"); return; }

            RebarBarType bt = FindBarType(c.Doc, f.BarTypeName);
            double otherDb = top ? c.Plan.OtherTransverseTopDb : c.Plan.OtherTransverseBottomDb;
            SectionBars.LongitudinalSet l = SectionBars.Longitudinal(s, cfg, top, Dia(c), c.Plan.SwapFootingLayers, otherDb);
            if (l.Span <= MinSeg) { c.Result.Failed.Add(name + ": no cabe en el ancho de zapata"); return; }

            var curves = new List<Curve> { Line.CreateBound(s.P(l.U0, l.V, wa), s.P(l.U0, l.V, wb)) };

            Place(c, name, bt, s.DirU, curves, l.Spacing, l.Span, Layout.Array, true);
        }

        // =================================================================
        // Reparto horizontal del alzado, por tramos de altura (StemZones). El alzado
        // va en talud, asi que o bien se coloca barra a barra (exacto) o por bandas
        // (menos elementos). En un esquinero la barra gira la esquina con una pata
        // de solape sobre la linea de barra de la otra ala (CornerFace).
        // =================================================================
        private static void StemHorizontals(Ctx c, bool back)
        {
            WallSection s = c.S;
            AppConfig cfg = c.Cfg;
            if (!(back ? cfg.StemHorizontalBackEnabled : cfg.StemHorizontalFrontEnabled)) return;
            string name = c.Plan.Label + "horizontal alzado " + (back ? "trasdos" : "intrados");

            ZoneLayout layout = c.Plan.Zones ?? StemZones.Resolve(s, cfg, n => FindBarType(c.Doc, n).BarNominalDiameter, c.Plan.ShiftHorizontals);
            if (layout.Error != null) { c.Result.Failed.Add(name + ": " + layout.Error); return; }

            RebarBarType btVert = FindBarType(c.Doc, (back ? cfg.StemVerticalBack : cfg.StemVerticalFront).BarTypeName);
            double cov = Mm(cfg.CoverStemMm) + btVert.BarNominalDiameter;

            bool useU0 = back ? s.HeelAtU0 : !s.HeelAtU0;
            Func<double, double> face = useU0 ? (Func<double, double>)s.FaceU0 : s.FaceU1;
            double sign = useU0 ? +1 : -1;
            CornerFace corner = useU0 ? c.Plan.CornerU0 : c.Plan.CornerU1;

            double wa = c.Plan.HorW0;
            double band = Mm(cfg.StemHorizontalBandMm);
            bool any = false;

            foreach (ResolvedZone z in layout.Zones)
            {
                ResolvedFace f = z.Face(back);
                if (f == null || f.Heights.Count == 0) continue;
                RebarBarType bt = FindBarType(c.Doc, f.BarTypeName);
                double db = bt.BarNominalDiameter;
                string zname = name + (layout.Zones.Count > 1 ? " tramo " + (z.Index + 1) : "");

                // Barra a la altura v (o a la altura media de su banda): tramo recto desde el
                // extremo libre y, si hay esquina en esta cara, pata sobre la otra ala.
                List<Curve> BarAt(double v)
                {
                    double u = face(v) + sign * (cov + db * 0.5);
                    double wEnd = corner != null ? corner.OtherBarW(v) : c.Plan.HorW1;
                    if (wEnd - wa <= MinSeg) return null;
                    var cl = new List<Curve> { Line.CreateBound(s.P(u, v, wa), s.P(u, v, wEnd)) };
                    double lap = corner != null ? corner.LapFor(db) : 0;
                    if (lap > MinSeg)
                        cl.Add(Line.CreateBound(s.P(u, v, wEnd), s.P(u + corner.LegDirU * lap, v, wEnd)));
                    return cl;
                }

                if (band <= MinSeg)
                {
                    foreach (double v in f.Heights)
                    {
                        List<Curve> cl = BarAt(v);
                        if (cl == null) continue;
                        any = true;
                        Place(c, zname + " v=" + ToMm(v) + " mm", bt, s.DirV, cl, 0, 0, Layout.Single, true);
                    }
                }
                else
                {
                    for (double v = f.VStart; v <= f.VEnd; v += band)
                    {
                        double vHi = Math.Min(v + band, f.VEnd);
                        double vMid = (v + vHi) * 0.5;
                        List<Curve> cl = BarAt(vMid);
                        if (cl == null) continue;
                        any = true;
                        // la barra de definicion va en la base de la banda: se desplaza desde vMid
                        Transform down = Transform.CreateTranslation(s.DirV * (v - vMid));
                        cl = cl.Select(cv => cv.CreateTransformed(down)).ToList();
                        Place(c, zname + " banda v=" + ToMm(v) + " mm", bt, s.DirV, cl, f.Spacing, vHi - v, Layout.Array, false);
                    }
                }
            }
            if (!any) c.Result.Failed.Add(name + ": no cabe ninguna barra en el tramo");
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

            Finish(c.Doc, rb, c.Partition(SetName(c, name)));
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

        /// <summary>Nombre del juego de barras para {conjunto}: sin la etiqueta del ala ni la cota de cada barra.</summary>
        private static string SetName(Ctx c, string name)
        {
            string n = name;
            if (!string.IsNullOrEmpty(c.Plan.Label) && n.StartsWith(c.Plan.Label)) n = n.Substring(c.Plan.Label.Length);
            n = System.Text.RegularExpressions.Regex.Replace(n, @"\s+(banda\s+)?v=.*$", "");
            return n.Trim();
        }
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
            var all = AllBarTypes(doc);
            if (all.Count == 0)
                throw new InvalidOperationException(
                    "El proyecto no tiene ningun tipo de barra (RebarBarType). Carga una familia de armadura primero.");

            string match = MatchName(all.Select(b => b.Name), name);
            return match != null ? all.First(b => b.Name == match) : all[0];
        }

        /// <summary>
        /// Nombre de tipo de barra que corresponde a "name": coincidencia exacta, si no
        /// parcial (sin distinguir mayusculas); null si no hay ninguna. La ventana usa la
        /// misma regla para que el preview muestre el diametro que despues se creara.
        /// </summary>
        public static string MatchName(IEnumerable<string> names, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var list = names.ToList();
            string exact = list.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            return list.FirstOrDefault(n => n.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>Tipos de barra del proyecto, ordenados por nombre (para la interfaz).</summary>
        public static List<RebarBarType> AllBarTypes(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(RebarBarType)).Cast<RebarBarType>()
                .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
