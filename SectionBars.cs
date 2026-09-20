using System;
using System.Collections.Generic;
using System.Linq;

namespace RetainingWallRebar
{
    /// <summary>
    /// Geometria en seccion (u, v) de las familias de armado fijas: verticales del alzado
    /// con sus patillas, bastones, transversales de zapata con sus patas, posiciones de
    /// las longitudinales y refuerzos de zapata. Funciones puras que usan el generador
    /// (para crear las barras) y el esquema de la ventana (para dibujarlas), asi lo que
    /// se ve es lo que se crea. Todo en pies, coordenadas locales del muro.
    ///
    /// Regla unica de apilado ("como lapices apilados"): eje a eje, la suma de los
    /// radios mas el hueco que se pida, y todo se mueve hacia dentro lo que haga falta.
    /// En cada capa de la zapata (superior o inferior), de fuera hacia dentro:
    ///   refuerzos por fuera (en el recubrimiento: mas alla no hay hormigon)
    ///   -> transversal (se apila sobre ellos si los hay)
    ///   -> refuerzos por dentro (sobre la transversal, con su hueco)
    ///   -> longitudinales (sobre la transversal, o sobre el refuerzo que les caiga encima)
    ///   -> patillas de las verticales (sobre lo mas interior de la capa inferior).
    /// Las patas de la transversal superior van por dentro de las de la inferior, y las
    /// longitudinales por dentro de las patas de su transversal. Las patillas de las
    /// verticales cruzan bajo la pantalla hacia el lado contrario y sobresalen LegMm de
    /// la cara opuesta; la del intrados va apilada sobre la del trasdos. En coronacion,
    /// la patilla opcional se dobla hacia la cara contraria (la del intrados un diametro
    /// mas abajo) y los horizontales del tramo superior paran debajo de ellas.
    ///
    /// Una familia activa cuyo tipo de barra no existe (diametro 0) se trata como si no
    /// existiera: no se dibuja ni influye en las demas. Asi el esquema de una
    /// configuracion recien abierta, sin tipos elegidos, muestra solo el hormigon.
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
            /// <summary>Desplazamiento hacia dentro respecto a su sitio nominal sobre la transversal (pies).</summary>
            public double Shift;
            /// <summary>Numero de barras con la regla de "separacion maxima" de Revit.</summary>
            public int Count => Span <= 0 || Spacing <= 0 ? 0 : (int)Math.Ceiling(Span / Spacing - 1e-9) + 1;
            public double UAt(int i) => U0 + (Count > 1 ? Span * i / (Count - 1) : 0);
        }

        /// <summary>
        /// Una capa de la zapata (superior o inferior) ya apilada. Las profundidades se
        /// miden desde la superficie del recubrimiento de esa cara hacia el interior de la
        /// zapata, asi las dos capas se calculan igual.
        /// </summary>
        public sealed class FootingLayer
        {
            public bool Top;
            public double FootingTop, Cover;
            /// <summary>Espesor de la capa exterior en modo "capas intercambiadas" (longitudinal fuera + transversal del otro ala); 0 si no.</summary>
            public double Outer;
            /// <summary>Diametros de transversal y longitudinal (0 si estan desactivadas).</summary>
            public double DbT, DbL;
            /// <summary>Profundidad del eje de la transversal / de las longitudinales (NaN si no existen).</summary>
            public double DT = double.NaN, DL = double.NaN;
            /// <summary>Lo que se han movido hacia dentro transversal y longitudinales respecto a su sitio nominal (pies).</summary>
            public double TransShift, LongShift;
            /// <summary>Profundidad del eje de cada refuerzo (mismo indice que la lista; NaN si es de la otra capa).</summary>
            public double[] DReinf = new double[0];
            /// <summary>Profundidad de la superficie mas interior de la capa (0 si esta vacia).</summary>
            public double DMax;

            /// <summary>Cota v del eje de una barra a la profundidad d.</summary>
            public double V(double d) => Top ? FootingTop - Cover - d : Cover + d;
        }

        private const double Tiny = 0.003;   // ~1 mm en pies

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

        private static double DbV(AppConfig cfg, bool back, Func<string, double> dia)
        {
            BarFamilyCfg f = back ? cfg.StemVerticalBack : cfg.StemVerticalFront;
            return f.Enabled ? dia(f.BarTypeName) : 0;
        }

        // =================================================================
        // Capas de la zapata
        // =================================================================

        /// <summary>
        /// Apila una capa de la zapata con todos sus refuerzos. Con swap (ala no pasante con
        /// malla cruzada) la longitudinal va por fuera y el resto de la capa se apoya sobre
        /// ella y sobre la transversal del otro ala que cruza el bloque.
        /// </summary>
        public static FootingLayer Layer(WallSection s, AppConfig cfg, bool top, Func<string, double> dia,
                                         bool swap = false, double otherTransDb = 0)
            => Layer(s, cfg, top, dia, cfg.FootingReinforcements, swap, otherTransDb);

        private static FootingLayer Layer(WallSection s, AppConfig cfg, bool top, Func<string, double> dia,
                                          IList<FootingReinfCfg> reinfs, bool swap, double otherTransDb)
        {
            var L = new FootingLayer
            {
                Top = top, FootingTop = s.FootingTop,
                Cover = Mm(top ? cfg.CoverFootingTopMm : cfg.CoverFootingBottomMm),
                DbT = DbT(cfg, top, dia), DbL = DbL(cfg, top, dia)
            };
            L.Outer = swap ? Math.Max(L.DbL, otherTransDb) : 0;

            int n = reinfs?.Count ?? 0;
            L.DReinf = new double[n];
            var db = new double[n];
            var gap = new double[n];
            var outward = new bool[n];
            var mine = new bool[n];
            for (int i = 0; i < n; i++)
            {
                L.DReinf[i] = double.NaN;
                FootingReinfCfg r = reinfs[i];
                mine[i] = r != null && r.Top == top;
                if (!mine[i]) continue;
                db[i] = dia(r.BarTypeName);
                gap[i] = Math.Max(0, Mm(r.GapMm));
                // "encima" es hacia fuera en la capa superior y hacia dentro en la inferior
                outward[i] = top ? r.Above : !r.Above;
            }

            // 1. refuerzos por fuera: se quedan en el recubrimiento (o sobre la capa exterior)
            //    y la transversal se apila por dentro de ellos con el hueco pedido
            double dT = L.Outer + L.DbT * 0.5, dTnom = dT;
            for (int i = 0; i < n; i++)
            {
                if (!mine[i] || !outward[i]) continue;
                L.DReinf[i] = L.Outer + db[i] * 0.5;
                if (L.DbT > 0) dT = Math.Max(dT, L.DReinf[i] + db[i] * 0.5 + gap[i] + L.DbT * 0.5);
            }
            if (L.DbT > 0) { L.DT = dT; L.TransShift = dT - dTnom; }

            // 2. refuerzos por dentro: sobre la transversal (o sobre la capa exterior si no hay)
            double inner = L.DbT > 0 ? dT + L.DbT * 0.5 : L.Outer;
            for (int i = 0; i < n; i++)
                if (mine[i] && !outward[i]) L.DReinf[i] = inner + gap[i] + db[i] * 0.5;

            // 3. longitudinales: sobre la transversal, apartadas por el refuerzo que les caiga encima
            if (L.DbL > 0)
            {
                if (swap) L.DL = L.DbL * 0.5;
                else
                {
                    // el desplazamiento se mide desde su sitio sin refuerzos (sobre la transversal en el recubrimiento)
                    double dL = inner + L.DbL * 0.5, dLnom = L.Outer + L.DbT + L.DbL * 0.5;
                    foreach (int i in Enumerable.Range(0, n).Where(i => mine[i] && !outward[i]).OrderBy(i => L.DReinf[i]))
                        if (Math.Abs(dL - L.DReinf[i]) < (L.DbL + db[i]) * 0.5 - 0.0005)
                            dL = L.DReinf[i] + db[i] * 0.5 + L.DbL * 0.5;
                    L.DL = dL;
                    L.LongShift = dL - dLnom;
                }
            }

            // 4. superficie mas interior de la capa
            L.DMax = L.Outer;
            if (L.DbT > 0) L.DMax = Math.Max(L.DMax, L.DT + L.DbT * 0.5);
            if (L.DbL > 0) L.DMax = Math.Max(L.DMax, L.DL + L.DbL * 0.5);
            for (int i = 0; i < n; i++)
                if (mine[i]) L.DMax = Math.Max(L.DMax, L.DReinf[i] + db[i] * 0.5);
            return L;
        }

        /// <summary>Transversal de zapata: pata, tramo horizontal, pata. null si esta desactivado.</summary>
        public static Poly Transverse(WallSection s, AppConfig cfg, bool top, Func<string, double> dia,
                                      bool swap = false, double otherTransDb = 0)
        {
            BarFamilyCfg f = top ? cfg.FootingTransverseTop : cfg.FootingTransverseBottom;
            if (!f.Enabled) return null;
            FootingLayer L = Layer(s, cfg, top, dia, swap, otherTransDb);
            double db = L.DbT;
            if (db <= 0) return null;   // sin tipo de barra
            double v = L.V(L.DT);

            // la superior va por dentro de las patas de la inferior
            double inset = top ? DbT(cfg, false, dia) : 0;
            double ua = Mm(cfg.CoverFootingSideMm) + inset + db * 0.5;
            double ub = s.LenU - ua;
            double leg = Mm(f.LegMm);
            double vLeg = v + (top ? -1 : +1) * leg;
            vLeg = Math.Min(Math.Max(vLeg, Mm(cfg.CoverFootingBottomMm) + db * 0.5), s.FootingTop - Mm(cfg.CoverFootingTopMm) - db * 0.5);

            var p = new Poly { Db = db };
            if (leg > Tiny) p.Pts.Add((ua, vLeg));
            p.Pts.Add((ua, v));
            p.Pts.Add((ub, v));
            if (leg > Tiny) p.Pts.Add((ub, vLeg));
            return p;
        }

        /// <summary>Longitudinales de zapata: cota, primera barra y ancho del array. null si esta desactivado.</summary>
        public static LongitudinalSet Longitudinal(WallSection s, AppConfig cfg, bool top, Func<string, double> dia,
                                                   bool swap = false, double otherTransDb = 0)
        {
            BarFamilyCfg f = top ? cfg.FootingLongitudinalTop : cfg.FootingLongitudinalBottom;
            if (!f.Enabled) return null;
            FootingLayer L = Layer(s, cfg, top, dia, swap, otherTransDb);
            double db = L.DbL;
            if (db <= 0) return null;   // sin tipo de barra

            // por dentro de las patas de su transversal (y la superior, ademas, de las de la inferior)
            double inset = L.DbT + (top ? DbT(cfg, false, dia) : 0);
            double u0 = Mm(cfg.CoverFootingSideMm) + inset + db * 0.5;
            return new LongitudinalSet { Db = db, V = L.V(L.DL), U0 = u0, Span = s.LenU - 2 * u0, Spacing = Mm(f.SpacingMm), Shift = L.LongShift };
        }

        /// <summary>
        /// Refuerzo transversal corto de zapata: barra recta apilada sobre su transversal
        /// (encima o debajo, tangente con GapMm = 0) y alineada con ella a lo largo del
        /// muro. Hacia fuera se queda en el recubrimiento y la transversal se aparta; hacia
        /// dentro apartan a las longitudinales. Puntera / talon: desde el borde de la zapata
        /// hacia dentro. Centro: desde el eje de la pantalla en su base, hacia cada lado. Se
        /// acota al ancho util de la zapata.
        /// </summary>
        public static Poly Reinforcement(WallSection s, AppConfig cfg, FootingReinfCfg r, Func<string, double> dia,
                                         out string warning, bool swap = false, double otherTransDb = 0)
        {
            warning = null;
            double db = dia(r.BarTypeName);
            if (db <= 0) return null;   // sin tipo de barra

            IList<FootingReinfCfg> list = cfg.FootingReinforcements;
            int idx = list?.IndexOf(r) ?? -1;
            if (idx < 0)
            {
                var tmp = new List<FootingReinfCfg>(list ?? new List<FootingReinfCfg>()) { r };
                list = tmp;
                idx = tmp.Count - 1;
            }
            FootingLayer L = Layer(s, cfg, r.Top, dia, list, swap, otherTransDb);
            double v = L.V(L.DReinf[idx]);

            // solo puede pasar en un canto minusculo o con un hueco desmesurado
            double vMin = Mm(cfg.CoverFootingBottomMm) + db * 0.5;
            double vMax = s.FootingTop - Mm(cfg.CoverFootingTopMm) - db * 0.5;
            if (v < vMin - Tiny || v > vMax + Tiny)
            {
                v = Math.Min(Math.Max(v, vMin), vMax);
                warning = "refuerzo " + r.Describe + ": no cabe en el canto de la zapata con ese hueco; se deja en el recubrimiento";
            }

            double lim0 = Mm(cfg.CoverFootingSideMm) + db * 0.5, lim1 = s.LenU - lim0;
            double ua, ub;
            bool heelAtU0 = s.HeelAtU0;
            if (r.IsCenter)
            {
                double axis = (s.FaceU0(s.FootingTop) + s.FaceU1(s.FootingTop)) * 0.5;
                double half = (s.FaceU1(s.FootingTop) - s.FaceU0(s.FootingTop)) * 0.5;
                double toe = Mm(r.ToeLengthMm), heel = Mm(r.HeelLengthMm);
                // la puntera esta al lado contrario del talon
                ua = heelAtU0 ? axis - heel : axis - toe;
                ub = heelAtU0 ? axis + toe : axis + heel;
                if (toe < half || heel < half)
                    warning = (warning != null ? warning + ". " : "") + "refuerzo " + r.Describe +
                              ": una longitud es menor que medio espesor de la pantalla (" +
                              WallSection.ToMm(half) + " mm) y la barra no asoma por esa cara";
            }
            else
            {
                double len = Mm(r.LengthMm);
                bool fromU0 = r.IsHeel ? heelAtU0 : !heelAtU0;   // borde desde el que se mide
                if (fromU0) { ua = lim0; ub = lim0 + len; }
                else { ub = lim1; ua = lim1 - len; }
            }
            double a = Math.Max(ua, lim0), b = Math.Min(ub, lim1);
            if (b - a < Mm(50)) { warning = "refuerzo " + r.Describe + ": longitud no valida"; return null; }
            if (a > ua + Tiny || b < ub - Tiny)
                warning = (warning != null ? warning + ". " : "") + "refuerzo " + r.Describe + ": se acorta al ancho util de la zapata";

            var p = new Poly { Db = db };
            p.Pts.Add((a, v));
            p.Pts.Add((b, v));
            return p;
        }

        // =================================================================
        // Verticales, patillas y bastones
        // =================================================================

        /// <summary>
        /// Cota del eje de la patilla inferior de una vertical: apoyada sobre lo mas interior
        /// de la capa inferior de la zapata; la del intrados apilada sobre la del trasdos.
        /// </summary>
        public static double HookLevel(WallSection s, AppConfig cfg, bool back, double db, Func<string, double> dia,
                                       bool swap = false, double otherTransDb = 0)
        {
            FootingLayer L = Layer(s, cfg, false, dia, swap, otherTransDb);
            double v = L.Cover + L.DMax;
            if (!back) v += DbV(cfg, true, dia);
            return v + db * 0.5;
        }

        private static bool HasCrown(BarFamilyCfg f) => f.Enabled && f.CrownLegMm > 0 && f.CutLengthMm <= 0;

        /// <summary>
        /// Cota del eje de la patilla de coronacion de una vertical: la del trasdos en el
        /// recubrimiento de coronacion, la del intrados un diametro mas abajo si el trasdos
        /// tambien lleva patilla (apiladas).
        /// </summary>
        public static double CrownLevel(WallSection s, AppConfig cfg, bool back, double db, Func<string, double> dia)
        {
            double v = s.LenV - Mm(cfg.CoverStemTopMm) - db * 0.5;
            if (!back && HasCrown(cfg.StemVerticalBack)) v -= dia(cfg.StemVerticalBack.BarTypeName);
            return v;
        }

        /// <summary>
        /// Cota por encima de la cual no puede haber horizontales del alzado: la cara
        /// inferior de la patilla de coronacion mas baja (las patillas cruzan de cara a
        /// cara). Sin patillas, el recubrimiento de coronacion.
        /// </summary>
        public static double StemTopLimit(WallSection s, AppConfig cfg, Func<string, double> dia)
        {
            double lim = s.LenV - Mm(cfg.CoverStemTopMm);
            for (int f = 0; f < 2; f++)
            {
                bool back = f == 0;
                BarFamilyCfg fv = back ? cfg.StemVerticalBack : cfg.StemVerticalFront;
                if (!HasCrown(fv)) continue;
                double db = dia(fv.BarTypeName);
                lim = Math.Min(lim, CrownLevel(s, cfg, back, db, dia) - db * 0.5);
            }
            return lim;
        }

        /// <summary>
        /// Cota por debajo de la cual no puede haber horizontales del alzado que bajen a la
        /// zapata: la cara superior de lo mas alto que hay bajo la pantalla en la capa
        /// inferior (patillas de las verticales incluidas).
        /// </summary>
        public static double StemBottomLimit(WallSection s, AppConfig cfg, Func<string, double> dia,
                                             bool swap = false, double otherTransDb = 0)
        {
            FootingLayer L = Layer(s, cfg, false, dia, swap, otherTransDb);
            double lim = L.Cover + L.DMax;
            for (int f = 0; f < 2; f++)
            {
                bool back = f == 0;
                BarFamilyCfg fv = back ? cfg.StemVerticalBack : cfg.StemVerticalFront;
                if (!fv.Enabled) continue;
                double db = dia(fv.BarTypeName);
                lim = Math.Max(lim, HookLevel(s, cfg, back, db, dia, swap, otherTransDb) + db * 0.5);
            }
            return lim;
        }

        private static double DowelTop(WallSection s, AppConfig cfg, BarFamilyCfg f, double db)
            => Math.Min(s.LenV - Mm(cfg.CoverStemTopMm) - db * 0.5, s.FootingTop + Mm(f.CutLengthMm));

        private static double DowelBottom(WallSection s, AppConfig cfg, BarFamilyCfg f, double db)
            => Math.Max(s.FootingTop - Mm(f.EmbedMm), Mm(cfg.CoverFootingBottomMm) + db * 0.5);

        /// <summary>
        /// Distancia desde la cara del alzado a la superficie sobre la que se apoya un
        /// horizontal de esa cara a la altura v: recubrimiento + vertical, y ademas hueco +
        /// baston si a esa altura hay un baston apilado por dentro de la vertical (el
        /// horizontal se aparta para apoyarse en el baston). db es el diametro del horizontal.
        /// </summary>
        public static double HorizontalOffset(WallSection s, AppConfig cfg, bool back, double v, double db, Func<string, double> dia)
        {
            double dbV = DbV(cfg, back, dia);
            double off = Mm(cfg.CoverStemMm) + dbV;
            BarFamilyCfg fd = back ? cfg.StemDowelBack : cfg.StemDowelFront;
            if (fd.Enabled && fd.Stacked)
            {
                double dbD = dia(fd.BarTypeName);
                double top = DowelTop(s, cfg, fd, dbD), bot = DowelBottom(s, cfg, fd, dbD);
                if (dbD > 0 && top > bot + Tiny && v + db * 0.5 > bot + Tiny && v - db * 0.5 < top - Tiny)
                    off += (dbV > 0 ? Math.Max(0, Mm(fd.GapMm)) : 0) + dbD;
            }
            return off;
        }

        /// <summary>
        /// Vertical del alzado de una cara: baja siguiendo la cara, se apoya sobre la
        /// parrilla inferior y su patilla cruza bajo la pantalla hasta sobresalir LegMm de
        /// la cara opuesta. Con CrownLegMm > 0 lleva ademas patilla de coronacion hacia la
        /// cara contraria, acotada a la vertical opuesta. null si esta desactivada.
        /// </summary>
        public static Poly Vertical(WallSection s, AppConfig cfg, bool back, Func<string, double> dia,
                                    bool swap = false, double otherTransDb = 0)
        {
            BarFamilyCfg f = back ? cfg.StemVerticalBack : cfg.StemVerticalFront;
            if (!f.Enabled) return null;
            double db = dia(f.BarTypeName);
            if (db <= 0) return null;   // sin tipo de barra
            double cov = Mm(cfg.CoverStemMm);

            bool useU0 = back ? s.HeelAtU0 : !s.HeelAtU0;
            double sign = useU0 ? +1 : -1;               // hacia el interior del alzado
            double uAt(double v) => (useU0 ? s.FaceU0(v) : s.FaceU1(v)) + sign * (cov + db * 0.5);

            bool crown = HasCrown(f);
            double vTop = crown ? CrownLevel(s, cfg, back, db, dia) : s.LenV - Mm(cfg.CoverStemTopMm) - db * 0.5;
            if (f.CutLengthMm > 0) vTop = Math.Min(vTop, s.FootingTop + Mm(f.CutLengthMm));
            double vBot = HookLevel(s, cfg, back, db, dia, swap, otherTransDb);
            if (vTop <= vBot + Tiny) return null;

            var p = new Poly { Db = db };

            // patilla de coronacion: hacia la cara contraria, como mucho hasta la vertical opuesta
            // (tangente a ella si a esa altura sigue subiendo; hasta su linea si ya ha doblado)
            if (crown)
            {
                BarFamilyCfg fo = back ? cfg.StemVerticalFront : cfg.StemVerticalBack;
                double dbO = fo.Enabled ? dia(fo.BarTypeName) : db;
                bool oppContinues = fo.Enabled && (!back || !HasCrown(fo));
                double oppFace = useU0 ? s.FaceU1(vTop) : s.FaceU0(vTop);
                double uLim = oppFace - sign * (cov + (oppContinues ? dbO : dbO * 0.5));
                double uCrown = uAt(vTop) + sign * Mm(f.CrownLegMm);
                uCrown = sign > 0 ? Math.Min(uCrown, uLim) : Math.Max(uCrown, uLim);
                if (Math.Abs(uCrown - uAt(vTop)) > Tiny) p.Pts.Add((uCrown, vTop));
            }

            p.Pts.Add((uAt(vTop), vTop));
            p.Pts.Add((uAt(vBot), vBot));

            // cruza hacia el lado contrario y sobresale LegMm de la cara opuesta del alzado
            double opposite = useU0 ? s.FaceU1(s.FootingTop) : s.FaceU0(s.FootingTop);
            double uEnd = opposite + sign * Mm(f.LegMm);
            double lim = Mm(cfg.CoverFootingSideMm) + DbT(cfg, false, dia) + db;
            uEnd = Math.Min(Math.Max(uEnd, lim), s.LenU - lim);
            if (Math.Abs(uEnd - uAt(vBot)) > Tiny) p.Pts.Add((uEnd, vBot));
            return p;
        }

        /// <summary>
        /// Baston de arranque de una cara: barra recta que nace dentro de la zapata con una
        /// longitud de anclaje EmbedMm bajo su cara superior y se corta a CutLengthMm sobre
        /// la zapata. Sin patilla. Apilado por dentro de la vertical de su cara (tangente,
        /// con hueco opcional) o, si no, en la misma linea que ella (intercalado). El anclaje
        /// se recorta al recubrimiento inferior si no cabe (con aviso). null si esta desactivado.
        /// </summary>
        public static Poly Dowel(WallSection s, AppConfig cfg, bool back, Func<string, double> dia, out string warning)
        {
            warning = null;
            BarFamilyCfg f = back ? cfg.StemDowelBack : cfg.StemDowelFront;
            if (!f.Enabled) return null;
            double db = dia(f.BarTypeName);
            if (db <= 0) return null;   // sin tipo de barra
            double dbV = DbV(cfg, back, dia);
            double off = Mm(cfg.CoverStemMm) + (f.Stacked && dbV > 0 ? dbV + Math.Max(0, Mm(f.GapMm)) : 0) + db * 0.5;

            bool useU0 = back ? s.HeelAtU0 : !s.HeelAtU0;
            double sign = useU0 ? +1 : -1;
            double uAt(double v) => (useU0 ? s.FaceU0(v) : s.FaceU1(v)) + sign * off;

            double vTop = DowelTop(s, cfg, f, db);
            double vBot = s.FootingTop - Mm(f.EmbedMm);
            double vMin = Mm(cfg.CoverFootingBottomMm) + db * 0.5;
            if (vBot < vMin - Tiny)
            {
                vBot = vMin;
                warning = "baston " + (back ? "trasdos" : "intrados") + ": el anclaje de " + f.EmbedMm.ToString("0") +
                          " mm no cabe en la zapata; se recorta a " + WallSection.ToMm(s.FootingTop - vBot) + " mm";
            }
            if (vTop <= vBot + Tiny)
            {
                warning = "baston " + (back ? "trasdos" : "intrados") + ": altura no valida";
                return null;
            }

            var p = new Poly { Db = db };
            p.Pts.Add((uAt(vTop), vTop));
            p.Pts.Add((uAt(vBot), vBot));
            return p;
        }
    }
}
