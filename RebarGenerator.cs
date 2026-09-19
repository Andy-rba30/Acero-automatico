using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace RetainingWallRebar
{
    public static class RebarGenerator
    {
        private static double Mm(double mm) => WallSection.Mm(mm);
        private const double MinSeg = 0.003; // ~1 mm en pies

        public static int Build(Document doc, Element host, WallSection s, AppConfig cfg)
        {
            int n = 0;
            string partition = PartitionName(doc, host);

            n += StemVerticals(doc, host, s, cfg, partition, back: true);
            n += StemVerticals(doc, host, s, cfg, partition, back: false);
            n += FootingTransverse(doc, host, s, cfg, partition, top: false);
            n += FootingTransverse(doc, host, s, cfg, partition, top: true);
            n += FootingLongitudinal(doc, host, s, cfg, partition, top: false);
            n += FootingLongitudinal(doc, host, s, cfg, partition, top: true);
            n += StemHorizontals(doc, host, s, cfg, partition, back: true);
            n += StemHorizontals(doc, host, s, cfg, partition, back: false);
            return n;
        }

        // =================================================================
        // Verticales del alzado: tramo inclinado siguiendo la cara + patilla en zapata
        // =================================================================
        private static int StemVerticals(Document doc, Element host, WallSection s, AppConfig cfg,
                                         string partition, bool back)
        {
            BarFamilyCfg f = back ? cfg.StemVerticalBack : cfg.StemVerticalFront;
            if (!f.Enabled) return 0;

            RebarBarType bt = FindBarType(doc, f.BarTypeName);
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

            XYZ pTop = s.P(uAt(vTop), vTop, 0);
            XYZ pBend = s.P(uAt(vBot), vBot, 0);

            // patilla horizontal hacia el interior de la zapata, acotada por el canto util
            double leg = Mm(f.LegMm);
            double uBend = uAt(vBot);
            double uEnd = uBend - sign * leg;           // cruza hacia el lado opuesto
            double uLim0 = Mm(cfg.CoverFootingSideMm) + db;
            double uLim1 = s.LenU - Mm(cfg.CoverFootingSideMm) - db;
            uEnd = Math.Min(Math.Max(uEnd, uLim0), uLim1);
            XYZ pLeg = s.P(uEnd, vBot, 0);

            var curves = new List<Curve>();
            AddLine(curves, pTop, pBend);
            AddLine(curves, pBend, pLeg);
            if (curves.Count == 0) return 0;

            Rebar r = Create(doc, host, bt, s.DirW, curves);
            if (r == null) return 0;

            ArrayAlong(r, Mm(f.SpacingMm), s.LenW - 2 * Mm(cfg.CoverEndMm));
            Finish(doc, r, partition, s, cfg);
            return 1;
        }

        // =================================================================
        // Armado transversal de zapata (superior e inferior) con patas en extremos
        // =================================================================
        private static int FootingTransverse(Document doc, Element host, WallSection s, AppConfig cfg,
                                             string partition, bool top)
        {
            BarFamilyCfg f = top ? cfg.FootingTransverseTop : cfg.FootingTransverseBottom;
            if (!f.Enabled) return 0;

            RebarBarType bt = FindBarType(doc, f.BarTypeName);
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

            var curves = new List<Curve>();
            if (leg > MinSeg) AddLine(curves, s.P(ua, vLeg, 0), s.P(ua, v, 0));
            AddLine(curves, s.P(ua, v, 0), s.P(ub, v, 0));
            if (leg > MinSeg) AddLine(curves, s.P(ub, v, 0), s.P(ub, vLeg, 0));
            if (curves.Count == 0) return 0;

            Rebar r = Create(doc, host, bt, s.DirW, curves);
            if (r == null) return 0;

            ArrayAlong(r, Mm(f.SpacingMm), s.LenW - 2 * Mm(cfg.CoverEndMm));
            Finish(doc, r, partition, s, cfg);
            return 1;
        }

        // =================================================================
        // Reparto longitudinal de zapata: una sola barra definida + array en U
        // =================================================================
        private static int FootingLongitudinal(Document doc, Element host, WallSection s, AppConfig cfg,
                                               string partition, bool top)
        {
            BarFamilyCfg f = top ? cfg.FootingLongitudinalTop : cfg.FootingLongitudinalBottom;
            if (!f.Enabled) return 0;

            RebarBarType bt = FindBarType(doc, f.BarTypeName);
            double db = bt.BarNominalDiameter;
            RebarBarType btTrans = FindBarType(doc, (top ? cfg.FootingTransverseTop : cfg.FootingTransverseBottom).BarTypeName);

            // se apoya por dentro del transversal
            double v = top
                ? s.FootingTop - Mm(cfg.CoverFootingTopMm) - btTrans.BarNominalDiameter - db * 0.5
                : Mm(cfg.CoverFootingBottomMm) + btTrans.BarNominalDiameter + db * 0.5;

            double u0 = Mm(cfg.CoverFootingSideMm) + db * 0.5;
            double span = s.LenU - 2 * u0;
            if (span <= MinSeg) return 0;

            double wa = Mm(cfg.CoverEndMm);
            double wb = s.LenW - wa;
            var curves = new List<Curve> { Line.CreateBound(s.P(u0, v, wa), s.P(u0, v, wb)) };

            Rebar r = Create(doc, host, bt, s.DirU, curves);
            if (r == null) return 0;

            r.GetShapeDrivenAccessor().SetLayoutAsMaximumSpacing(Mm(f.SpacingMm), span, true, true, true);
            Finish(doc, r, partition, s, cfg);
            return 1;
        }

        // =================================================================
        // Reparto horizontal del alzado. El alzado va en talud, asi que o bien
        // se coloca barra a barra (exacto) o por bandas (menos elementos).
        // =================================================================
        private static int StemHorizontals(Document doc, Element host, WallSection s, AppConfig cfg,
                                           string partition, bool back)
        {
            BarFamilyCfg f = back ? cfg.StemHorizontalBack : cfg.StemHorizontalFront;
            if (!f.Enabled) return 0;

            RebarBarType bt = FindBarType(doc, f.BarTypeName);
            double db = bt.BarNominalDiameter;
            RebarBarType btVert = FindBarType(doc, (back ? cfg.StemVerticalBack : cfg.StemVerticalFront).BarTypeName);
            double cov = Mm(cfg.CoverStemMm) + btVert.BarNominalDiameter;

            bool useU0 = back ? s.HeelAtU0 : !s.HeelAtU0;
            Func<double, double> face = useU0 ? (Func<double, double>)s.FaceU0 : s.FaceU1;
            double sign = useU0 ? +1 : -1;

            double spacing = Mm(f.SpacingMm);
            double vStart = s.FootingTop + spacing * 0.5;
            double vEnd = s.LenV - Mm(cfg.CoverStemTopMm) - db;
            if (vEnd <= vStart) return 0;

            double wa = Mm(cfg.CoverEndMm);
            double wb = s.LenW - wa;
            double band = Mm(cfg.StemHorizontalBandMm);

            int count = 0;
            if (band <= MinSeg)
            {
                for (double v = vStart; v <= vEnd; v += spacing)
                {
                    double u = face(v) + sign * (cov + db * 0.5);
                    var c = new List<Curve> { Line.CreateBound(s.P(u, v, wa), s.P(u, v, wb)) };
                    Rebar r = Create(doc, host, bt, s.DirV, c);
                    if (r == null) continue;
                    r.GetShapeDrivenAccessor().SetLayoutAsSingle();
                    Finish(doc, r, partition, s, cfg);
                    count++;
                }
            }
            else
            {
                for (double v = vStart; v <= vEnd; v += band)
                {
                    double vHi = Math.Min(v + band, vEnd);
                    double vMid = (v + vHi) * 0.5;
                    double u = face(vMid) + sign * (cov + db * 0.5);
                    var c = new List<Curve> { Line.CreateBound(s.P(u, v, wa), s.P(u, v, wb)) };
                    Rebar r = Create(doc, host, bt, s.DirV, c);
                    if (r == null) continue;
                    r.GetShapeDrivenAccessor().SetLayoutAsMaximumSpacing(spacing, vHi - v, true, true, false);
                    Finish(doc, r, partition, s, cfg);
                    count++;
                }
            }
            return count;
        }

        // =================================================================
        // Utilidades
        // =================================================================
        private static void AddLine(List<Curve> list, XYZ a, XYZ b)
        {
            if (a.DistanceTo(b) > MinSeg) list.Add(Line.CreateBound(a, b));
        }

        private static Rebar Create(Document doc, Element host, RebarBarType bt, XYZ normal, IList<Curve> curves)
        {
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
            catch (Exception)
            {
                return null;
            }
        }

        private static void ArrayAlong(Rebar r, double spacing, double length)
        {
            if (length <= spacing) return;
            r.GetShapeDrivenAccessor().SetLayoutAsMaximumSpacing(spacing, length, true, true, true);
        }

        private static void Finish(Document doc, Rebar r, string partition, WallSection s, AppConfig cfg)
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
