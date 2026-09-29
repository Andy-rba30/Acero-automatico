using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace ColumnRebar
{
    /// <summary>Un conjunto (elemento Rebar) creado para una columna.</summary>
    public sealed class CreatedSet
    {
        public ElementId Id;
        public string Name;
        /// <summary>Radio nominal de la barra (pies).</summary>
        public double Radius;
        /// <summary>Barra longitudinal: se comprueba solo el tramo dentro de la altura de la columna (puede sobresalir a proposito).</summary>
        public bool Longitudinal;
    }

    /// <summary>Resultado del armado de un elemento.</summary>
    public sealed class BuildResult
    {
        public List<CreatedSet> Created = new List<CreatedSet>();
        /// <summary>Barras que quedarian fuera del hormigon. Si hay alguna, el elemento entero se deshace.</summary>
        public List<string> Rejected = new List<string>();
        /// <summary>Conjuntos que Revit no pudo crear.</summary>
        public List<string> Failed = new List<string>();
        public List<string> Warnings = new List<string>();
        public int Bars, StirrupSets, TieSets;
        public string Summary => Bars + " longitudinales, " + StirrupSets + " tramos de estribos" + (TieSets > 0 ? ", " + TieSets + " tramos de grapas" : "");
        public bool Safe => Rejected.Count == 0;
    }

    public static class RebarGenerator
    {
        private static double Mm(double mm) => ColumnSection.Mm(mm);
        private static double ToMm(double ft) => ColumnSection.ToMm(ft);
        private const double MinSeg = 0.003;   // ~1 mm en pies
        /// <summary>Longitud de barra que se tolera fuera del solido al comprobar (pies, ~1 mm).</summary>
        private const double InsideTol = 0.0033;

        private sealed class Ctx
        {
            public Document Doc;
            public HostAnalysis Item;
            public ColumnSection S;
            public AppConfig Cfg;
            public BuildResult Result;
            public ColumnPlan Plan;
            public List<StirrupRun> Runs;
            public double Tol;
            /// <summary>Orientacion de los ganchos de estribos y grapas (se invierte sola si queda fuera del hormigon).</summary>
            public bool StirrupHookLeft, TieHookLeft, StirrupHookChecked, TieHookChecked;
            public ElementId StirrupHook = ElementId.InvalidElementId, TieHook = ElementId.InvalidElementId;
        }

        // =================================================================
        // Armado de una columna
        // =================================================================
        public static BuildResult Build(Document doc, HostAnalysis item, AppConfig cfg)
        {
            var c = new Ctx
            {
                Doc = doc, Item = item, S = item.Section, Cfg = cfg, Result = new BuildResult(),
                Tol = Mm(cfg.PrismCheckToleranceMm),
                StirrupHookLeft = cfg.HookLeft, TieHookLeft = cfg.TieHookLeft
            };

            RebarBarType btLong = FindBarType(doc, cfg.Longitudinal.BarTypeName, "longitudinales");
            RebarBarType btStirrup = FindBarType(doc, cfg.Stirrups.BarTypeName, "estribos");
            RebarBarType btTie = cfg.Crossties.Enabled ? FindBarType(doc, cfg.Crossties.BarTypeName, "grapas") : null;
            c.StirrupHook = FindHookType(doc, cfg.Stirrups.HookTypeName);
            c.TieHook = cfg.Crossties.Enabled ? FindHookType(doc, cfg.Crossties.HookTypeName) : ElementId.InvalidElementId;

            c.Plan = PlanFor(item, cfg, btLong.BarNominalDiameter, btStirrup.BarNominalDiameter,
                             btTie?.BarNominalDiameter ?? 0);
            if (c.Plan.Error != null) throw new InvalidOperationException(c.Plan.Error);
            c.Result.Warnings.AddRange(c.Plan.Warnings);

            c.Runs = RunsFor(item, cfg, out string warn);
            if (warn != null) c.Result.Warnings.Add(warn);
            if (c.Runs.Count == 0) throw new InvalidOperationException("la distribucion de estribos no produce ningun estribo");

            Longitudinals(c, btLong);
            if (!c.Result.Safe) return c.Result;
            Stirrups(c, btStirrup);
            if (!c.Result.Safe) return c.Result;
            if (btTie != null) Ties(c, btTie);
            return c.Result;
        }

        /// <summary>Armado de la seccion con esta configuracion (lo mismo que dibuja la ventana).</summary>
        public static ColumnPlan PlanFor(HostAnalysis item, AppConfig cfg, double dbFt, double dsFt, double dtFt)
        {
            ColumnSection s = item.Section;
            return ColumnPlan.Build(s.Polygon, s.Rects, Mm(cfg.CoverMm), dsFt, dbFt, Mm(item.MaxSpacingMm(cfg)),
                                    cfg.Crossties.Enabled && cfg.TiesU, cfg.Crossties.Enabled && cfg.TiesV, dtFt,
                                    Mm(cfg.PrismCheckToleranceMm));
        }

        /// <summary>Tramos de estribos de esta columna con esta configuracion. Lanza si la distribucion no se entiende.</summary>
        public static List<StirrupRun> RunsFor(HostAnalysis item, AppConfig cfg, out string warning)
        {
            List<StirrupGroup> groups = StirrupLayout.Parse(item.Distribution(cfg), out string err);
            if (groups == null) throw new InvalidOperationException("distribucion de estribos \"" + item.Distribution(cfg) + "\": " + err);
            return StirrupLayout.Runs(item.Section.Height, Mm(cfg.Stirrups.BottomOffsetMm), Mm(cfg.Stirrups.TopOffsetMm),
                                      groups, cfg.Stirrups.Symmetric, out warning);
        }

        // -----------------------------------------------------------------
        // Longitudinales: una fila equiespaciada = un conjunto con array a lo largo de u
        // -----------------------------------------------------------------
        private static void Longitudinals(Ctx c, RebarBarType bt)
        {
            ColumnSection s = c.S;
            LongitudinalCfg L = c.Cfg.Longitudinal;
            double zBot = s.ZBase - Mm(L.BottomExtensionMm);
            double zTop = s.ZTop + Mm(L.TopExtensionMm);
            double leg = L.BottomExtensionMm > 0 ? Mm(L.BottomLegMm) : 0;
            if (L.BottomLegMm > 0 && L.BottomExtensionMm <= 0)
                c.Result.Warnings.Add("la patilla inferior se ignora porque las barras no sobresalen por debajo de la base");
            if (zTop - zBot < MinSeg) { c.Result.Rejected.Add("longitudinales sin longitud"); return; }
            Pt centroid = Rectilinear.Centroid(s.Polygon);

            foreach ((Pt first, int count, double step) in c.Plan.Rows(c.Tol))
            {
                XYZ normal;
                XYZ legDir = null;
                if (count > 1)
                {
                    normal = s.DirU;
                    if (leg > 0) legDir = s.DirV * (centroid.V >= first.V ? 1 : -1);
                }
                else
                {
                    double du = centroid.U - first.U, dv = centroid.V - first.V;
                    if (leg > 0 && Math.Abs(du) > Math.Abs(dv)) { legDir = s.DirU * (du >= 0 ? 1 : -1); normal = s.DirV; }
                    else { if (leg > 0) legDir = s.DirV * (dv >= 0 ? 1 : -1); normal = s.DirU; }
                }

                XYZ bottom = s.World(first.U, first.V, zBot);
                XYZ top = s.World(first.U, first.V, zTop);
                var curves = new List<Curve>();
                if (leg > 0) AddLine(curves, bottom + legDir * leg, bottom);
                AddLine(curves, bottom, top);

                string name = "longitudinal v=" + ToMm(first.V) + (count > 1 ? " (" + count + " barras cada " + ToMm(step) + " mm)" : " u=" + ToMm(first.U));
                Place(c, name, bt, RebarStyle.Standard, ElementId.InvalidElementId, true, normal, curves, count, step, longitudinal: true, stirrup: "");
                c.Result.Bars += count;
            }
        }

        // -----------------------------------------------------------------
        // Estribos cerrados: un conjunto por rectangulo y tramo de la distribucion
        // -----------------------------------------------------------------
        private static void Stirrups(Ctx c, RebarBarType bt)
        {
            ColumnSection s = c.S;
            foreach (PlanStirrup st in c.Plan.Stirrups)
            {
                Rect r = st.Line;
                foreach (StirrupRun run in c.Runs)
                {
                    double z = s.ZBase + run.Z0;
                    // antihorario visto desde arriba, empezando y acabando en la esquina superior izquierda (ahi van los ganchos)
                    XYZ p1 = s.World(r.U1, r.V2, z), p2 = s.World(r.U1, r.V1, z);
                    XYZ p3 = s.World(r.U2, r.V1, z), p4 = s.World(r.U2, r.V2, z);
                    var curves = new List<Curve>();
                    AddLine(curves, p1, p2); AddLine(curves, p2, p3); AddLine(curves, p3, p4); AddLine(curves, p4, p1);
                    string name = "estribo " + (st.Index + 1) + " " + run.Label;
                    bool ok = Place(c, name, bt, RebarStyle.StirrupTie, c.StirrupHook, c.StirrupHookLeft, XYZ.BasisZ, curves,
                                    run.Count, run.Spacing, longitudinal: false, stirrup: (st.Index + 1).ToString(),
                                    checkHooks: c.StirrupHook != ElementId.InvalidElementId && !c.StirrupHookChecked,
                                    flip: () => c.StirrupHookLeft = !c.StirrupHookLeft);
                    if (c.StirrupHook != ElementId.InvalidElementId) c.StirrupHookChecked = true;
                    if (!ok) return;
                    c.Result.StirrupSets++;
                }
            }
        }

        // -----------------------------------------------------------------
        // Grapas: una por par de intermedias enfrentadas, en cada cota de estribo
        // -----------------------------------------------------------------
        private static void Ties(Ctx c, RebarBarType bt)
        {
            ColumnSection s = c.S;
            int n = 0;
            foreach (PlanTie t in c.Plan.Ties)
            {
                n++;
                foreach (StirrupRun run in c.Runs)
                {
                    double z = s.ZBase + run.Z0;
                    var curves = new List<Curve>();
                    AddLine(curves, s.World(t.A.U, t.A.V, z), s.World(t.B.U, t.B.V, z));
                    string name = "grapa " + n + " estribo " + (t.Stirrup + 1) + " " + run.Label;
                    bool ok = Place(c, name, bt, RebarStyle.StirrupTie, c.TieHook, c.TieHookLeft, XYZ.BasisZ, curves,
                                    run.Count, run.Spacing, longitudinal: false, stirrup: (t.Stirrup + 1).ToString(),
                                    checkHooks: c.TieHook != ElementId.InvalidElementId && !c.TieHookChecked,
                                    flip: () => c.TieHookLeft = !c.TieHookLeft);
                    if (c.TieHook != ElementId.InvalidElementId) c.TieHookChecked = true;
                    if (!ok) return;
                    c.Result.TieSets++;
                }
            }
        }

        // =================================================================
        // Colocacion con red de seguridad
        // =================================================================

        /// <summary>
        /// Comprueba que la barra (y todas las posiciones del array) queda dentro del
        /// hormigon y solo entonces la crea. Con "checkHooks", tras crearla lee su geometria
        /// real (ganchos incluidos); si los ganchos asoman, la borra, invierte la orientacion
        /// (flip) y la vuelve a crear. False si algo se rechazo.
        /// </summary>
        private static bool Place(Ctx c, string name, RebarBarType bt, RebarStyle style, ElementId hook, bool hookLeft,
                                  XYZ normal, List<Curve> curves, int count, double spacing, bool longitudinal, string stirrup,
                                  bool checkHooks = false, Action flip = null)
        {
            normal = normal.Normalize();
            bool array = count >= 2 && spacing > MinSeg;
            double r = bt.BarNominalDiameter * 0.5;

            // --- RED DE SEGURIDAD (1): geometria planificada, antes de crear nada ---
            for (int k = 0; k < (array ? count : 1); k++)
            {
                IList<Curve> moved = curves;
                if (k > 0)
                {
                    Transform t = Transform.CreateTranslation(normal * (k * spacing));
                    moved = curves.Select(cv => cv.CreateTransformed(t)).ToList();
                }
                if (!BarInside(c.S, moved, r, longitudinal, out string why))
                {
                    c.Result.Rejected.Add(name + (k > 0 ? " (posicion " + (k + 1) + " del array)" : "") + ": " + why);
                    return false;
                }
            }

            for (int attempt = 0; attempt < 2; attempt++)
            {
                Rebar rb = Create(c.Doc, c.S.Host, bt, style, hook, hookLeft, normal, curves, out string err);
                if (rb == null) { c.Result.Failed.Add(name + ": Revit no pudo crear la barra (" + err + ")"); return true; }

                if (array) rb.GetShapeDrivenAccessor().SetLayoutAsFixedNumber(count, (count - 1) * spacing, true, true, true);
                else rb.GetShapeDrivenAccessor().SetLayoutAsSingle();

                if (checkHooks && flip != null)
                {
                    // RED DE SEGURIDAD (1b): los ganchos solo existen en la geometria real
                    c.Doc.Regenerate();
                    if (!RealInside(c.S, rb, r, longitudinal, out string why))
                    {
                        c.Doc.Delete(rb.Id);
                        if (attempt == 0)
                        {
                            flip();
                            hookLeft = !hookLeft;
                            c.Result.Warnings.Add(name + ": los ganchos quedaban fuera del hormigon, se ha invertido su orientacion");
                            continue;
                        }
                        c.Result.Rejected.Add(name + ": " + why + " (con las dos orientaciones de gancho)");
                        return false;
                    }
                }

                Finish(c.Doc, rb, c.Item.Partition(c.Cfg, SetName(name), stirrup));
                c.Result.Created.Add(new CreatedSet { Id = rb.Id, Name = name, Radius = r, Longitudinal = longitudinal });
                return true;
            }
            return false;
        }

        /// <summary>
        /// RED DE SEGURIDAD (2): tras crear y regenerar, se lee la geometria REAL de cada
        /// barra de cada conjunto tal y como la ha colocado Revit (radios de doblado, ganchos
        /// y todas las posiciones del array) y se comprueba contra el solido. Las
        /// longitudinales se comprueban solo en la altura de la columna (pueden sobresalir
        /// a proposito por arriba y por abajo).
        /// </summary>
        public static void VerifyCreated(Document doc, ColumnSection s, BuildResult res)
        {
            foreach (CreatedSet cs in res.Created)
            {
                var rb = doc.GetElement(cs.Id) as Rebar;
                if (rb == null) { res.Rejected.Add(cs.Name + ": el conjunto no existe tras regenerar"); continue; }
                if (!RealInside(s, rb, cs.Radius, cs.Longitudinal, out string why)) res.Rejected.Add(cs.Name + ": " + why);
            }
        }

        private static bool RealInside(ColumnSection s, Rebar rb, double r, bool longitudinal, out string why)
        {
            why = null;
            int n;
            try { n = rb.NumberOfBarPositions; }
            catch (Exception ex) { why = "no se pudo leer el conjunto (" + ex.Message + ")"; return false; }
            for (int k = 0; k < n; k++)
            {
                IList<Curve> cl;
                try
                {
                    if (!rb.DoesBarExistAtPosition(k)) continue;
                    cl = rb.GetCenterlineCurves(false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, k);
                }
                catch (Exception ex) { why = "barra " + (k + 1) + " de " + n + ": no se pudo leer su geometria (" + ex.Message + ")"; return false; }
                if (cl == null || cl.Count == 0) { why = "barra " + (k + 1) + " de " + n + ": sin geometria"; return false; }
                if (!BarInside(s, cl, r, longitudinal, out string w)) { why = "barra " + (k + 1) + " de " + n + ": " + w; return false; }
            }
            return true;
        }

        /// <summary>
        /// True si toda la barra queda dentro del solido. Ademas del eje se comprueban fibras
        /// extremas (eje desplazado +-r en cada direccion local; en las longitudinales solo en
        /// el plano de la seccion), asi una barra tangente a una cara o con medio diametro
        /// fuera tambien falla. Las longitudinales se recortan a la altura de la columna.
        /// </summary>
        private static bool BarInside(ColumnSection s, IList<Curve> curves, double r, bool longitudinal, out string why)
        {
            why = null;
            var shifts = new List<XYZ> { XYZ.Zero, s.DirU * r, s.DirU * -r, s.DirV * r, s.DirV * -r };
            if (!longitudinal) { shifts.Add(XYZ.BasisZ * r); shifts.Add(XYZ.BasisZ * -r); }

            IEnumerable<Curve> toCheck = longitudinal
                ? curves.SelectMany(cv => ClipToRange(cv, s.ZBase + InsideTol, s.ZTop - InsideTol))
                : curves;
            foreach (Curve cv in toCheck)
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
            return true;
        }

        /// <summary>Trozos de la curva (como lineas) con z entre z0 y z1; lo que queda fuera de ese rango no se comprueba.</summary>
        private static List<Curve> ClipToRange(Curve cv, double z0, double z1)
        {
            var result = new List<Curve>();
            IList<XYZ> pts = cv.Tessellate();
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                XYZ a = pts[i], b = pts[i + 1];
                if (Math.Max(a.Z, b.Z) < z0 || Math.Min(a.Z, b.Z) > z1) continue;
                XYZ lo = a, hi = b;
                if (Math.Abs(b.Z - a.Z) > 1e-12)
                {
                    double ta = Math.Max(0, Math.Min(1, (z0 - a.Z) / (b.Z - a.Z)));
                    double tb = Math.Max(0, Math.Min(1, (z1 - a.Z) / (b.Z - a.Z)));
                    double t0 = Math.Min(ta, tb), t1 = Math.Max(ta, tb);
                    if (a.Z < z0 || a.Z > z1) lo = a + (b - a) * (a.Z < b.Z ? t0 : t1);
                    if (b.Z < z0 || b.Z > z1) hi = a + (b - a) * (a.Z < b.Z ? t1 : t0);
                }
                if (lo.DistanceTo(hi) > MinSeg) result.Add(Line.CreateBound(lo, hi));
            }
            return result;
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
        private static string SetName(string name) =>
            System.Text.RegularExpressions.Regex.Replace(name, @"\s+(v=|u=|\(|inf|sup|resto).*$", "").Trim();

        private static void AddLine(List<Curve> list, XYZ a, XYZ b)
        {
            if (a.DistanceTo(b) > MinSeg) list.Add(Line.CreateBound(a, b));
        }

        private static Rebar Create(Document doc, Element host, RebarBarType bt, RebarStyle style, ElementId hook, bool hookLeft,
                                    XYZ normal, IList<Curve> curves, out string err)
        {
            err = null;
            try
            {
                // Revit 2027: ganchos y tratamientos de extremo van agrupados en BarTerminationsData.
                using (BarTerminationsData term = new BarTerminationsData(doc))
                {
                    if (hook != null && hook != ElementId.InvalidElementId)
                    {
                        term.HookTypeIdAtStart = hook;
                        term.HookTypeIdAtEnd = hook;
                    }
                    RebarTerminationOrientation o = hookLeft ? RebarTerminationOrientation.Left : RebarTerminationOrientation.Right;
                    term.TerminationOrientationAtStart = o;
                    term.TerminationOrientationAtEnd = o;
                    return Rebar.CreateFromCurves(doc, style, bt, host, normal.Normalize(), curves, term, true, true);
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
            try { r.SetUnobscuredInView(doc.ActiveView, true); } catch { }
        }

        public static RebarBarType FindBarType(Document doc, string name, string use)
        {
            var all = AllBarTypes(doc);
            if (all.Count == 0)
                throw new InvalidOperationException("El proyecto no tiene ningun tipo de barra (RebarBarType). Carga una familia de armadura primero.");
            string match = MatchName(all.Select(b => b.Name), name);
            if (match == null)
                throw new InvalidOperationException("el tipo de barra de " + use + " \"" + name + "\" no existe en este proyecto; elige uno de los cargados en la ventana");
            return all.First(b => b.Name == match);
        }

        /// <summary>Id del tipo de gancho, o InvalidElementId si el nombre esta vacio. Lanza si el nombre no existe.</summary>
        public static ElementId FindHookType(Document doc, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return ElementId.InvalidElementId;
            var all = AllHookTypes(doc);
            string match = MatchName(all.Select(h => h.Name), name);
            if (match == null)
                throw new InvalidOperationException("el tipo de gancho \"" + name + "\" no existe en este proyecto; elige uno de los cargados en la ventana o deja el gancho vacio");
            return all.First(h => h.Name == match).Id;
        }

        /// <summary>
        /// Nombre que corresponde a "name": coincidencia exacta, si no parcial (sin distinguir
        /// mayusculas); null si no hay ninguna. Nunca se sustituye por otro: sin coincidencia no se arma.
        /// </summary>
        public static string MatchName(IEnumerable<string> names, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var list = names.ToList();
            string exact = list.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            return list.FirstOrDefault(n => n.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static List<RebarBarType> AllBarTypes(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).Cast<RebarBarType>()
                .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();

        public static List<RebarHookType> AllHookTypes(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(RebarHookType)).Cast<RebarHookType>()
                .OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
