using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RetainingWallRebar
{
    /// <summary>
    /// Modo "seguir la forma cortada" para un muro al que otro elemento le ha quitado
    /// hormigon: cada familia de barras se limita al tramo donde existe SU parte del
    /// hormigon en el solido cortado (alzado o zapata) y, en el extremo cortado, se
    /// prolonga una longitud de traslape dentro del otro elemento. Las barras se siguen
    /// comprobando contra el solido completo, porque el traslape queda dentro del
    /// volumen que Revit le ha dado al otro.
    /// </summary>
    public static class CrossingWall
    {
        /// <summary>
        /// Donde conserva el solido cortado el alzado y la zapata enteros, en w del muro
        /// completo. Un extremo "cortado" es el que no coincide con el extremo del muro:
        /// ahi es donde se aplica el traslape en vez del recubrimiento de extremo.
        /// </summary>
        public sealed class CutParts
        {
            public double StemA, StemB, FootA, FootB;
            public bool StemCutA, StemCutB, FootCutA, FootCutB;

            public string Describe()
            {
                string mm(double ft) => WallSection.ToMm(ft).ToString("0");
                return "alzado w=" + mm(StemA) + ".." + mm(StemB) + " mm, zapata w=" + mm(FootA) + ".." + mm(FootB) + " mm";
            }
        }

        /// <summary>
        /// Muestrea el solido cortado a lo largo del eje, por separado el alzado (v por
        /// encima de la cara superior de zapata) y la zapata (v por debajo), y compara el
        /// volumen de cada rebanada con el de la geometria completa. Una estacion cuenta
        /// como entera solo si conserva practicamente todo el hormigon de esa parte (un
        /// corte parcial del ancho cuenta como cortada: lo conservador). Cada parte tiene
        /// que quedar en un unico tramo contiguo; si el otro elemento la parte en dos, o
        /// no queda nada de ella, el modo se rechaza con el motivo. Los limites se afinan
        /// con la cara plana perpendicular al eje del elemento que corta o, si no la hay,
        /// con la ultima estacion entera.
        /// </summary>
        public static CutParts Detect(WallSection full, Solid cut, AppConfig cfg, out string why)
        {
            why = null;
            if (cut == null) { why = "no hay solido cortado"; return null; }

            double tol = WallSection.Tol(cfg), step = WallSection.Step(cfg), half = WallSection.HalfSlice(cfg);
            int n = (int)Math.Ceiling(full.LenW / step);
            n = Math.Max(5, Math.Min(n, 500));

            WallSection probe = full.Stretch(0, full.LenW, cut, "");
            double eps = tol;

            Solid Part(WallSection s, bool stem, double w) =>
                stem ? WallSection.Intersect(s, -1, s.LenU + 1, s.FootingTop + eps, s.LenV + 1, w - half, w + half)
                     : WallSection.Intersect(s, -1, s.LenU + 1, -1, s.FootingTop - eps, w - half, w + half);

            // referencia: la rebanada central del solido completo (es un prisma: vale para todas)
            Solid refStem = Part(full, true, full.LenW * 0.5), refFoot = Part(full, false, full.LenW * 0.5);
            if (refStem == null || refFoot == null) { why = "no se pudo leer alzado o zapata en la geometria completa"; return null; }
            double vStem = refStem.Volume, vFoot = refFoot.Volume;

            var stemOk = new bool[n];
            var footOk = new bool[n];
            for (int i = 0; i < n; i++)
            {
                double w = full.LenW * (i + 0.5) / n;
                Solid a = Part(probe, true, w), b = Part(probe, false, w);
                stemOk[i] = a != null && a.Volume >= vStem * 0.99;
                footOk[i] = b != null && b.Volume >= vFoot * 0.99;
            }

            var res = new CutParts();
            if (!Range(full, cut, stemOk, n, step, tol, "el alzado", out res.StemA, out res.StemB, out res.StemCutA, out res.StemCutB, out why)) return null;
            if (!Range(full, cut, footOk, n, step, tol, "la zapata", out res.FootA, out res.FootB, out res.FootCutA, out res.FootCutB, out why)) return null;
            return res;
        }

        private static bool Range(WallSection full, Solid cut, bool[] ok, int n, double step, double tol, string what,
                                  out double a, out double b, out bool cutA, out bool cutB, out string why)
        {
            a = b = 0; cutA = cutB = false; why = null;
            int i0 = Array.IndexOf(ok, true);
            if (i0 < 0) { why = what + " no queda entero en ninguna estacion"; return false; }
            int i1 = i0;
            while (i1 < n && ok[i1]) i1++;
            if (Array.IndexOf(ok, true, i1) >= 0)
            { why = "el otro elemento parte " + what + " en dos trozos; usa pasante o parar en el cruce"; return false; }

            double w0 = full.Origin.DotProduct(full.DirW);
            cutA = i0 > 0;
            cutB = i1 < n;
            a = !cutA ? 0 : WallSection.FaceNear(cut, full.DirW, w0, full.LenW * i0 / n, step * 0.5 + tol) ?? full.LenW * (i0 + 0.5) / n;
            b = !cutB ? full.LenW : WallSection.FaceNear(cut, full.DirW, w0, full.LenW * i1 / n, step * 0.5 + tol) ?? full.LenW * (i1 - 0.5) / n;
            if (b - a < WallSection.Mm(100)) { why = what + " entero es demasiado corto (" + WallSection.ToMm(b - a) + " mm)"; return false; }
            return true;
        }

        /// <summary>
        /// Plan de un tramo recto en modo "seguir la forma cortada": verticales, bastones y
        /// horizontales en el tramo del alzado; transversales, refuerzos y longitudinales en
        /// el de la zapata. En un extremo libre se aplica el recubrimiento de extremo; en un
        /// extremo cortado la familia se prolonga el traslape (misma regla que en la esquina:
        /// max(CornerLapDiameters x diametro, CornerLapMinMm), con el mayor diametro de las
        /// familias del grupo), sin salir nunca del muro completo.
        /// </summary>
        public static WingPlan FollowPlan(Document doc, WallSection s, CutParts parts, AppConfig cfg)
        {
            double cov = WallSection.Mm(cfg.CoverEndMm);
            double lapDia = cfg.CornerLapDiameters, lapMin = WallSection.Mm(cfg.CornerLapMinMm);

            double Db(BarFamilyCfg f) => f != null && f.Enabled ? DbOf(doc, f.BarTypeName) : 0;
            double dbVert = Math.Max(Math.Max(Db(cfg.StemVerticalBack), Db(cfg.StemVerticalFront)),
                                     Math.Max(Db(cfg.StemDowelBack), Db(cfg.StemDowelFront)));
            double dbHor = 0;
            foreach (StemZoneCfg z in cfg.StemHorizontalZones)
            {
                if (cfg.StemHorizontalBackEnabled) dbHor = Math.Max(dbHor, DbOf(doc, z.BackBarTypeName));
                if (cfg.StemHorizontalFrontEnabled) dbHor = Math.Max(dbHor, DbOf(doc, z.SameBothFaces ? z.BackBarTypeName : z.FrontBarTypeName));
            }
            double dbTrans = Math.Max(Db(cfg.FootingTransverseTop), Db(cfg.FootingTransverseBottom));
            foreach (FootingReinfCfg r in cfg.FootingReinforcements ?? new List<FootingReinfCfg>())
                dbTrans = Math.Max(dbTrans, DbOf(doc, r.BarTypeName));
            double dbLong = Math.Max(Db(cfg.FootingLongitudinalTop), Db(cfg.FootingLongitudinalBottom));

            (double a, double b) Span(double pa, double pb, bool cutA, bool cutB, double db)
            {
                double lap = Math.Max(lapMin, lapDia * db);
                double a = cutA ? Math.Max(cov, pa - lap) : pa + cov;
                double b = cutB ? Math.Min(s.LenW - cov, pb + lap) : pb - cov;
                return (a, b);
            }

            var vert = Span(parts.StemA, parts.StemB, parts.StemCutA, parts.StemCutB, dbVert);
            var hor = Span(parts.StemA, parts.StemB, parts.StemCutA, parts.StemCutB, dbHor);
            var trans = Span(parts.FootA, parts.FootB, parts.FootCutA, parts.FootCutB, dbTrans);
            var lng = Span(parts.FootA, parts.FootB, parts.FootCutA, parts.FootCutB, dbLong);

            return new WingPlan
            {
                Label = string.IsNullOrEmpty(s.Label) ? "" : s.Label + " ",
                VertW0 = vert.a, VertW1 = vert.b,
                HorW0 = hor.a, HorW1 = hor.b,
                TransW0 = trans.a, TransW1 = trans.b,
                LongW0 = lng.a, LongW1 = lng.b
            };
        }

        /// <summary>Diametro nominal del tipo de barra (pies), 0 si no hay tipo o no existe en el proyecto.</summary>
        private static double DbOf(Document doc, string barTypeName)
        {
            if (string.IsNullOrWhiteSpace(barTypeName)) return 0;
            try { return RebarGenerator.FindBarType(doc, barTypeName).BarNominalDiameter; }
            catch { return 0; }
        }

        /// <summary>Texto del traslape para el diagnostico.</summary>
        public static string LapText(AppConfig cfg) =>
            "traslape en el corte " + cfg.CornerLapDiameters.ToString("0.#") + " diametros, minimo " +
            cfg.CornerLapMinMm.ToString("0") + " mm";
    }
}
