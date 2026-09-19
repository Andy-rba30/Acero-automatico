using System;
using System.Collections.Generic;

namespace RetainingWallRebar
{
    /// <summary>
    /// Geometria en seccion (u, v) de las familias de armado fijas: verticales del alzado
    /// con su patilla, transversales de zapata con sus patas y posiciones de las
    /// longitudinales de zapata. Funciones puras que usan el generador (para crear las
    /// barras) y el esquema de la ventana (para dibujarlas), asi lo que se ve es lo que
    /// se crea. Todo en pies, coordenadas locales del muro.
    ///
    /// Capas en la zapata, de fuera hacia dentro (igual arriba que abajo):
    ///   transversal -> longitudinal -> patillas de las verticales.
    /// Las patas de la transversal superior van por dentro de las de la inferior, y las
    /// longitudinales por dentro de las patas de su transversal, para que nada se cruce
    /// en la misma linea. Las patillas de las verticales cruzan bajo la pantalla hacia el
    /// lado contrario y sobresalen LegMm de la cara opuesta; la del intrados va apilada
    /// sobre la del trasdos.
    /// </summary>
    public static class SectionBars
    {
        public sealed class Poly
        {
            public List<(double u, double v)> Pts = new List<(double u, double v)>();
            public double Db;
        }

        public sealed class LongitudinalSet
        {
            public double Db, V, U0, Span, Spacing;
            /// <summary>Numero de barras con la regla de "separacion maxima" de Revit.</summary>
            public int Count => Span <= 0 || Spacing <= 0 ? 0 : (int)Math.Ceiling(Span / Spacing - 1e-9) + 1;
            public double UAt(int i) => U0 + (Count > 1 ? Span * i / (Count - 1) : 0);
        }

        private static double Mm(double mm) => WallSection.Mm(mm);

        /// <summary>Diametro del transversal si esta activo, 0 si no.</summary>
        private static double DbT(AppConfig cfg, bool top, Func<string, double> dia)
        {
            BarFamilyCfg f = top ? cfg.FootingTransverseTop : cfg.FootingTransverseBottom;
            return f.Enabled ? dia(f.BarTypeName) : 0;
        }

        private static double DbL(AppConfig cfg, bool top, Func<string, double> dia)
        {
            BarFamilyCfg f = top ? cfg.FootingLongitudinalTop : cfg.FootingLongitudinalBottom;
            return f.Enabled ? dia(f.BarTypeName) : 0;
        }

        /// <summary>
        /// Cotas v del eje del transversal y del longitudinal de una capa. Con swap (ala no
        /// pasante con malla cruzada) el longitudinal va por fuera y el transversal por
        /// dentro, dejando sitio al transversal del otro ala que cruza el bloque.
        /// </summary>
        public static void FootingLevels(WallSection s, AppConfig cfg, bool top, double dbT, double dbL,
                                         bool swap, double otherTransDb, out double vTrans, out double vLong)
        {
            double cov = top ? Mm(cfg.CoverFootingTopMm) : Mm(cfg.CoverFootingBottomMm);
            double offT, offL;
            if (!swap) { offT = dbT * 0.5; offL = dbT + dbL * 0.5; }
            else { offL = dbL * 0.5; offT = Math.Max(dbL, otherTransDb) + dbT * 0.5; }
            vTrans = top ? s.FootingTop - cov - offT : cov + offT;
            vLong = top ? s.FootingTop - cov - offL : cov + offL;
        }

        /// <summary>Transversal de zapata: pata, tramo horizontal, pata. null si esta desactivado.</summary>
        public static Poly Transverse(WallSection s, AppConfig cfg, bool top, Func<string, double> dia,
                                      bool swap = false, double otherTransDb = 0)
        {
            BarFamilyCfg f = top ? cfg.FootingTransverseTop : cfg.FootingTransverseBottom;
            if (!f.Enabled) return null;
            double db = dia(f.BarTypeName);
            FootingLevels(s, cfg, top, db, DbL(cfg, top, dia), swap, otherTransDb, out double v, out _);

            // la superior va por dentro de las patas de la inferior
            double inset = top ? DbT(cfg, false, dia) : 0;
            double ua = Mm(cfg.CoverFootingSideMm) + inset + db * 0.5;
            double ub = s.LenU - ua;
            double leg = Mm(f.LegMm);
            double vLeg = v + (top ? -1 : +1) * leg;
            vLeg = Math.Min(Math.Max(vLeg, Mm(cfg.CoverFootingBottomMm) + db * 0.5), s.FootingTop - Mm(cfg.CoverFootingTopMm) - db * 0.5);

            var p = new Poly { Db = db };
            if (leg > 0.003) p.Pts.Add((ua, vLeg));
            p.Pts.Add((ua, v));
            p.Pts.Add((ub, v));
            if (leg > 0.003) p.Pts.Add((ub, vLeg));
            return p;
        }

        /// <summary>Longitudinales de zapata: cota, primera barra y ancho del array. null si esta desactivado.</summary>
        public static LongitudinalSet Longitudinal(WallSection s, AppConfig cfg, bool top, Func<string, double> dia,
                                                   bool swap = false, double otherTransDb = 0)
        {
            BarFamilyCfg f = top ? cfg.FootingLongitudinalTop : cfg.FootingLongitudinalBottom;
            if (!f.Enabled) return null;
            double db = dia(f.BarTypeName);
            double dbT = DbT(cfg, top, dia);
            FootingLevels(s, cfg, top, dbT, db, swap, otherTransDb, out _, out double v);

            // por dentro de las patas de su transversal (y la superior, ademas, de las de la inferior)
            double inset = dbT + (top ? DbT(cfg, false, dia) : 0);
            double u0 = Mm(cfg.CoverFootingSideMm) + inset + db * 0.5;
            return new LongitudinalSet { Db = db, V = v, U0 = u0, Span = s.LenU - 2 * u0, Spacing = Mm(f.SpacingMm) };
        }

        /// <summary>
        /// Vertical del alzado de una cara: baja siguiendo la cara, se apoya sobre la
        /// parrilla inferior y su patilla cruza bajo la pantalla hasta sobresalir LegMm de
        /// la cara opuesta. null si esta desactivada.
        /// </summary>
        public static Poly Vertical(WallSection s, AppConfig cfg, bool back, Func<string, double> dia)
        {
            BarFamilyCfg f = back ? cfg.StemVerticalBack : cfg.StemVerticalFront;
            if (!f.Enabled) return null;
            double db = dia(f.BarTypeName);
            double cov = Mm(cfg.CoverStemMm);

            bool useU0 = back ? s.HeelAtU0 : !s.HeelAtU0;
            double sign = useU0 ? +1 : -1;               // hacia el interior del alzado
            double uAt(double v) => (useU0 ? s.FaceU0(v) : s.FaceU1(v)) + sign * (cov + db * 0.5);

            double vTop = s.LenV - Mm(cfg.CoverStemTopMm) - db * 0.5;
            if (f.CutLengthMm > 0) vTop = Math.Min(vTop, s.FootingTop + Mm(f.CutLengthMm));

            // patilla apoyada sobre transversal + longitudinal inferiores; la del intrados
            // apilada sobre la del trasdos
            double mesh = Mm(cfg.CoverFootingBottomMm) + DbT(cfg, false, dia) + DbL(cfg, false, dia);
            double vBot = mesh + db * 0.5;
            if (!back && cfg.StemVerticalBack.Enabled) vBot += dia(cfg.StemVerticalBack.BarTypeName);

            // cruza hacia el lado contrario y sobresale LegMm de la cara opuesta del alzado
            double opposite = useU0 ? s.FaceU1(s.FootingTop) : s.FaceU0(s.FootingTop);
            double uEnd = opposite + sign * Mm(f.LegMm);
            double lim = Mm(cfg.CoverFootingSideMm) + DbT(cfg, false, dia) + db;
            uEnd = Math.Min(Math.Max(uEnd, lim), s.LenU - lim);

            var p = new Poly { Db = db };
            p.Pts.Add((uAt(vTop), vTop));
            p.Pts.Add((uAt(vBot), vBot));
            if (Math.Abs(uEnd - uAt(vBot)) > 0.003) p.Pts.Add((uEnd, vBot));
            return p;
        }
    }
}
