using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace RetainingWallRebar
{
    /// <summary>
    /// Muro esquinero en L (en planta): dos alas perpendiculares que comparten un bloque
    /// de esquina. Cada ala se describe con una WallSection cuyo tramo [0, LenW] es solo
    /// su tramo recto, medido desde el extremo libre del ala hacia la esquina; a
    /// continuacion (w de LenW a LenW+BlockLen) esta el bloque de esquina, que es la
    /// zapata de la otra ala mas los alzados que lo atraviesan.
    ///
    /// Como se detecta (ver Detect): para cada eje horizontal de la familia se corta el
    /// solido en estaciones equiespaciadas. Las estaciones cuya seccion NO abarca todo
    /// el ancho del bounding box son el tramo recto de un ala (deben ser contiguas,
    /// tocar un solo extremo y tener todas la misma seccion); el resto es el bloque de
    /// esquina, cuya longitud tiene que coincidir con el ancho de zapata de la otra ala.
    /// Cada ala se lee despues como un tramo recto normal (canto, caras) en su propia
    /// rebanada central, y se mide hasta donde llega su alzado dentro del bloque.
    ///
    /// Reglas de armado de la esquina (ver BuildPlans y README):
    ///  - Un ala es la "pasante": sus verticales llegan hasta la cara exterior del alzado
    ///    de la otra ala y su malla de zapata atraviesa el bloque. La otra ala para sus
    ///    verticales en la cara del alzado pasante y su malla en la cara del bloque.
    ///  - Los horizontales de ambas alas giran la esquina en L con una pata de solape
    ///    sobre la linea de barra de la otra ala (cara exterior con exterior, interior con
    ///    interior). Los del ala no pasante se suben un diametro para no cruzarse.
    ///  - Todas las barras se siguen comprobando contra el solido completo de la L.
    /// </summary>
    public sealed class CornerWall
    {
        /// <summary>Ala 0 = a lo largo del eje X de la familia, ala 1 = eje Y.</summary>
        public WallSection[] Wings = new WallSection[2];

        public Solid Solid;

        /// <summary>Eleccion manual del ala pasante para este elemento: -1 = segun configuracion, 0 / 1 = ala.</summary>
        public int ThroughChoice = -1;

        public static string LastError;

        private static double Mm(double mm) => WallSection.Mm(mm);
        private static double ToMm(double ft) => WallSection.ToMm(ft);

        public int ThroughIndex(AppConfig cfg)
        {
            if (ThroughChoice == 0 || ThroughChoice == 1) return ThroughChoice;
            int c = cfg.CornerThroughWingIndex;
            if (c == 0 || c == 1) return c;
            return Wings[0].LenW >= Wings[1].LenW ? 0 : 1;
        }

        public string Describe(AppConfig cfg)
        {
            int t = ThroughIndex(cfg);
            return "esquinero en L: ala 1 (" + Wings[0].Describe() + "), ala 2 (" + Wings[1].Describe() +
                   "), ala pasante = " + (t + 1);
        }

        // ==================================================================
        // Deteccion
        // ==================================================================

        /// <summary>Resultado del muestreo de un eje.</summary>
        private sealed class AxisScan
        {
            public WallSection Wing;         // ala cuyo tramo recto va a lo largo de este eje
            public string Why;
        }

        public static CornerWall Detect(Document doc, Element host, Solid solid, AppConfig cfg)
        {
            LastError = null;
            try
            {
                return DetectCore(doc, host, solid, cfg);
            }
            catch (Exception ex)
            {
                LastError = "error al analizar la L: " + ex.Message;
                return null;
            }
        }

        private static CornerWall DetectCore(Document doc, Element host, Solid solid, AppConfig cfg)
        {
            List<XYZ> pts = WallSection.Vertices(solid);
            if (pts.Count < 4) { LastError = "el solido no tiene aristas legibles"; return null; }

            XYZ[] axes = WallSection.PlanAxes(host);
            double tol = WallSection.Tol(cfg);
            var ov = cfg.OverrideFor(WallSection.TypeNameOf(doc, host));

            var cw = new CornerWall { Solid = solid };
            for (int k = 0; k < 2; k++)
            {
                AxisScan sc = ScanAxis(solid, pts, axes[k], cfg, tol);
                if (sc.Wing == null)
                {
                    LastError = "no es una L simple a lo largo del eje " + (k == 0 ? "X" : "Y") + " de la familia: " + sc.Why;
                    return null;
                }
                sc.Wing.Label = "ala " + (k + 1);
                cw.Wings[k] = sc.Wing;
            }

            // --- coherencia entre las dos alas ---
            WallSection a = cw.Wings[0], b = cw.Wings[1];
            double geoTol = Math.Max(WallSection.Step(cfg), tol * 4);
            if (Math.Abs(a.BlockLen - b.LenU) > geoTol || Math.Abs(b.BlockLen - a.LenU) > geoTol)
            {
                LastError = "las alas no encajan como una L: bloque del ala 1 = " + ToMm(a.BlockLen) + " mm frente a ancho de zapata del ala 2 = " +
                            ToMm(b.LenU) + " mm; bloque del ala 2 = " + ToMm(b.BlockLen) + " mm frente a ancho del ala 1 = " + ToMm(a.LenU) + " mm";
                return null;
            }
            // el bloque de esquina visto desde cada ala tiene que ser el mismo trozo de hormigon
            XYZ ca = a.P(a.LenU * 0.5, 0, a.LenW + a.BlockLen * 0.5);
            XYZ cb = b.P(b.LenU * 0.5, 0, b.LenW + b.BlockLen * 0.5);
            if (ca.DistanceTo(cb) > geoTol)
            {
                LastError = "el bloque de esquina no coincide visto desde las dos alas (" + ToMm(ca.DistanceTo(cb)) + " mm de diferencia)";
                return null;
            }

            // --- cada ala tiene que ser un tramo recto en su tramo [0, LenW] ---
            for (int k = 0; k < 2; k++)
            {
                WallSection w = cw.Wings[k];
                if (w.LenW < Mm(200))
                { LastError = w.Label + ": tramo recto demasiado corto (" + ToMm(w.LenW) + " mm)"; return null; }
                if (!WallSection.SectionIsConstant(w, cfg, tol, out string why))
                { LastError = w.Label + " no es un prisma recto en su tramo: " + why; return null; }
                if (!WallSection.ReadSection(w, cfg, ov, out string err))
                { LastError = w.Label + ": " + err; return null; }
            }

            // --- lado en el que queda la otra ala y alcance del alzado dentro del bloque ---
            for (int k = 0; k < 2; k++)
            {
                WallSection w = cw.Wings[k], o = cw.Wings[1 - k];
                XYZ otherRunCenter = o.P(o.LenU * 0.5, 0, o.LenW * 0.5);
                double uo = w.ToLocal(otherRunCenter).X;
                if (uo > -tol && uo < w.LenU + tol)
                { LastError = w.Label + ": el tramo recto de la otra ala cae dentro de su propio ancho de zapata"; return null; }
                w.OtherSideU = uo > w.LenU ? +1 : -1;
                w.StemReach = StemReach(w, cfg);
            }
            return cw;
        }

        /// <summary>
        /// Muestrea el solido a lo largo de un eje y separa tramo recto (secciones que no
        /// abarcan todo el ancho) de bloque de esquina (las que si). Devuelve el ala cuyo
        /// tramo recto va a lo largo del eje, orientada desde el extremo libre hacia el bloque.
        /// </summary>
        private static AxisScan ScanAxis(Solid solid, List<XYZ> pts, XYZ c, AppConfig cfg, double tol)
        {
            var res = new AxisScan();
            XYZ dirV = XYZ.BasisZ;
            XYZ dirU = dirV.CrossProduct(c).Normalize();

            double u0 = pts.Min(p => p.DotProduct(dirU)), u1 = pts.Max(p => p.DotProduct(dirU));
            double v0 = pts.Min(p => p.DotProduct(dirV)), v1 = pts.Max(p => p.DotProduct(dirV));
            double w0 = pts.Min(p => p.DotProduct(c)), w1 = pts.Max(p => p.DotProduct(c));

            var f = new WallSection
            {
                DirU = dirU, DirV = dirV, DirW = c,
                Origin = dirU * u0 + dirV * v0 + c * w0,
                LenU = u1 - u0, LenV = v1 - v0, LenW = w1 - w0,
                HostSolid = solid
            };

            double step = WallSection.Step(cfg);
            int n = (int)Math.Ceiling(f.LenW / step);
            n = Math.Max(8, Math.Min(n, 500));
            double half = WallSection.HalfSlice(cfg);

            var samples = new WallSection.SectionSample[n];
            var isRun = new bool[n];
            double fullTol = Math.Max(Mm(20), tol * 4);
            for (int i = 0; i < n; i++)
            {
                double w = f.LenW * (i + 0.5) / n;
                samples[i] = WallSection.SampleSection(f, w, half);
                if (samples[i] == null)
                { res.Why = "no hay hormigon en la estacion w=" + ToMm(w) + " mm"; return res; }
                isRun[i] = (samples[i].U1 - samples[i].U0) < f.LenU - fullTol;
            }

            int runCount = isRun.Count(x => x);
            if (runCount == 0)
            { res.Why = "todas las secciones abarcan el ancho completo (no hay tramo recto de ala)"; return res; }
            if (runCount == n)
            { res.Why = "la seccion no abarca nunca el ancho completo (no hay bloque de esquina)"; return res; }

            bool runAtStart = isRun[0], runAtEnd = isRun[n - 1];
            if (runAtStart == runAtEnd)
            {
                res.Why = runAtStart
                    ? "el bloque de esquina queda en medio del recorrido (forma en T o en U)"
                    : "hay bloque de esquina en los dos extremos (forma en U o en Z)";
                return res;
            }
            // el tramo recto tiene que ser un unico bloque contiguo desde su extremo
            int runLen = 0;
            if (runAtStart) { while (runLen < n && isRun[runLen]) runLen++; }
            else { while (runLen < n && isRun[n - 1 - runLen]) runLen++; }
            if (runLen != runCount)
            { res.Why = "el tramo recto no es contiguo (hay mas de un cambio de seccion)"; return res; }

            // todas las secciones del tramo recto deben ser iguales
            int refIdx = runAtStart ? runLen / 2 : n - 1 - runLen / 2;
            WallSection.SectionSample r = samples[refIdx];
            double areaTol = WallSection.AreaTol(f, r, tol);
            for (int i = 0; i < n; i++)
            {
                if (!isRun[i]) continue;
                if (!WallSection.SameProfile(r, samples[i], tol, areaTol, out string diff))
                { res.Why = "la seccion del tramo recto no es constante: w=" + ToMm(samples[i].W) + " mm (" + diff + ")"; return res; }
            }

            // frontera tramo recto / bloque, afinada con una cara perpendicular al eje si la hay
            double boundary = runAtStart ? f.LenW * runLen / n : f.LenW * (n - runLen) / n;
            boundary = RefineBoundary(solid, c, w0, boundary, step);

            // --- ala orientada desde el extremo libre hacia el bloque ---
            XYZ dirW = runAtStart ? c : c.Negate();
            XYZ dirUw = dirV.CrossProduct(dirW).Normalize();
            bool sameU = dirUw.DotProduct(dirU) > 0;
            double uAbs0 = u0 + r.U0, uAbs1 = u0 + r.U1;           // en el eje dirU de f
            double uMin = sameU ? uAbs0 : -uAbs1;                    // en el eje dirUw del ala
            double runLenFt = runAtStart ? boundary : f.LenW - boundary;
            double wFree = runAtStart ? w0 : -w1;                    // extremo libre proyectado sobre dirW

            var wing = new WallSection
            {
                DirU = dirUw, DirV = dirV, DirW = dirW,
                Origin = dirUw * uMin + dirV * (v0 + r.V0) + dirW * wFree,
                LenU = r.U1 - r.U0,
                LenV = r.V1 - r.V0,
                LenW = runLenFt,
                BlockLen = f.LenW - runLenFt,
                HostSolid = solid
            };
            wing.StemReach = wing.LenW;
            res.Wing = wing;
            return res;
        }

        /// <summary>
        /// Si hay una cara plana perpendicular al eje cerca de la frontera muestreada (la
        /// cara interior de la zapata de la otra ala), se toma su posicion exacta.
        /// </summary>
        private static double RefineBoundary(Solid solid, XYZ c, double w0, double boundary, double step)
        {
            double best = boundary, bestD = step * 1.01;
            foreach (Face face in solid.Faces)
            {
                if (!(face is PlanarFace pf)) continue;
                if (Math.Abs(pf.FaceNormal.DotProduct(c)) < 0.999) continue;
                double w = pf.Origin.DotProduct(c) - w0;
                double d = Math.Abs(w - boundary);
                if (d < bestD) { bestD = d; best = w; }
            }
            return best;
        }

        /// <summary>
        /// Hasta donde llega el alzado de este ala dentro del bloque de esquina: se muestrea
        /// una franja fina del alzado (entre sus dos caras, a media altura) en estaciones del
        /// bloque; el alcance es la primera estacion sin hormigon (o el final del bloque).
        /// Conservador: si el alzado termina entre dos estaciones se toma el punto medio.
        /// </summary>
        private static double StemReach(WallSection s, AppConfig cfg)
        {
            if (s.BlockLen <= 0) return s.LenW;
            double half = WallSection.HalfSlice(cfg);
            double vProbe = s.FootingTop + (s.LenV - s.FootingTop) * 0.5;
            double fa = s.FaceU0(vProbe), fb = s.FaceU1(vProbe);
            double thick = fb - fa;
            if (thick <= Mm(20)) return s.LenW;
            double inset = Math.Min(Mm(20), thick * 0.25);
            double ua = fa + inset, ub = fb - inset;

            double step = WallSection.Step(cfg);
            int n = (int)Math.Ceiling(s.BlockLen / step);
            n = Math.Max(4, Math.Min(n, 200));
            for (int i = 0; i < n; i++)
            {
                double w = s.LenW + s.BlockLen * (i + 0.5) / n;
                Solid r = WallSection.Intersect(s, ua, ub, vProbe - half, vProbe + half, w - half, w + half);
                if (r == null) return i == 0 ? s.LenW : s.LenW + s.BlockLen * i / n;
            }
            return s.LenW + s.BlockLen;
        }

        // ==================================================================
        // Plan de armado de cada ala
        // ==================================================================

        /// <summary>w (en el marco de k) de un punto de la otra ala dado por su coordenada u.</summary>
        private static double OtherW(WallSection k, WallSection o, double uOther) => k.ToLocal(o.P(uOther, 0, 0)).Z;

        public WingPlan[] BuildPlans(Document doc, AppConfig cfg)
        {
            int through = ThroughIndex(cfg);
            double cov = Mm(cfg.CoverEndMm);
            double step = WallSection.Step(cfg);

            // el ala cuyo alzado ocupa la columna de esquina: la pasante si su alzado llega
            // a la cara exterior del alzado de la otra; si no, la otra si llega; si no, ninguna
            var reachesFar = new bool[2];
            var reachesNear = new bool[2];
            var near = new Func<double, double>[2];
            var far = new Func<double, double>[2];
            for (int k = 0; k < 2; k++)
            {
                WallSection s = Wings[k], o = Wings[1 - k];
                Func<double, double> w0 = v => OtherW(s, o, o.FaceU0(v));
                Func<double, double> w1 = v => OtherW(s, o, o.FaceU1(v));
                double vProbe = s.FootingTop + (s.LenV - s.FootingTop) * 0.5;
                bool w0IsNear = w0(vProbe) <= w1(vProbe);
                near[k] = w0IsNear ? w0 : w1;
                far[k] = w0IsNear ? w1 : w0;
                reachesNear[k] = s.StemReach >= near[k](vProbe) - step;
                reachesFar[k] = s.StemReach >= far[k](vProbe) - step;
            }
            int stemThrough = reachesFar[through] ? through : (reachesFar[1 - through] ? 1 - through : -1);

            // reparto de horizontales de cada ala (los del ala no pasante suben un diametro; con
            // malla cruzada sus patillas quedan mas altas y las barras que bajan a la zapata lo saben)
            Func<string, double> diameterFt = n => RebarGenerator.FindBarType(doc, n).BarNominalDiameter;
            double transBottomDb = cfg.FootingTransverseBottom.Enabled ? diameterFt(cfg.FootingTransverseBottom.BarTypeName) : 0;
            var layouts = new ZoneLayout[2];
            for (int k = 0; k < 2; k++)
            {
                bool swap = cfg.CornerFootingMeshBoth && k != through;
                layouts[k] = StemZones.Resolve(Wings[k], cfg, diameterFt, k != through, swap, transBottomDb);
            }

            var plans = new WingPlan[2];
            for (int k = 0; k < 2; k++)
            {
                WallSection s = Wings[k], o = Wings[1 - k];
                var p = new WingPlan { Label = s.Label + " ", Zones = layouts[k] };

                double vLo = s.FootingTop, vHi = s.LenV;
                double nearMin = Math.Min(near[k](vLo), near[k](vHi));
                double farMin = Math.Min(far[k](vLo), far[k](vHi));
                double farMax = Math.Max(far[k](vLo), far[k](vHi));

                // --- verticales del alzado ---
                double vertEnd;
                if (k == stemThrough)
                    vertEnd = (s.StemReach > farMax + step ? s.StemReach : farMin) - cov;
                else if (stemThrough == 1 - k)
                    vertEnd = Math.Min(s.StemReach, nearMin) - cov;
                else
                    vertEnd = s.StemReach - cov;
                p.VertW0 = cov;
                p.VertW1 = vertEnd;

                // --- horizontales del alzado: en L si este alzado llega al de la otra ala ---
                p.HorW0 = cov;
                p.HorW1 = Math.Min(s.StemReach, nearMin) - cov;
                if (reachesNear[k])
                {
                    p.HorW1 = s.StemReach - cov;
                    bool outerIsU0 = s.OtherSideU > 0;
                    bool otherOuterIsU0 = o.OtherSideU > 0;
                    p.CornerU0 = MakeCorner(cfg, s, o, layouts[1 - k], outerIsU0 ? otherOuterIsU0 : !otherOuterIsU0, diameterFt);
                    p.CornerU1 = MakeCorner(cfg, s, o, layouts[1 - k], outerIsU0 ? !otherOuterIsU0 : otherOuterIsU0, diameterFt);
                }
                p.ShiftHorizontals = k != through;

                // --- zapata ---
                double full = s.LenW + s.BlockLen - cov;
                double face = s.LenW - cov;
                if (cfg.CornerFootingMeshBoth)
                {
                    p.TransW0 = cov; p.TransW1 = full;
                    p.LongW0 = cov; p.LongW1 = face;
                    p.SwapFootingLayers = k != through;
                }
                else
                {
                    bool thr = k == through;
                    p.TransW0 = cov; p.TransW1 = thr ? full : face;
                    p.LongW0 = cov; p.LongW1 = thr ? full : face;
                }
                p.OtherTransverseTopDb = RebarGenerator.FindBarType(doc, cfg.FootingTransverseTop.BarTypeName).BarNominalDiameter;
                p.OtherTransverseBottomDb = RebarGenerator.FindBarType(doc, cfg.FootingTransverseBottom.BarTypeName).BarNominalDiameter;
                plans[k] = p;
            }
            return plans;
        }

        /// <summary>
        /// Esquina para los horizontales de una cara de s: la pata sigue la linea de barra
        /// horizontal de la otra ala en su cara emparejada (otherU0: cara u=min de la otra
        /// ala). El diametro de esa barra depende del tramo de altura en el que este, y su
        /// apoyo (vertical o baston apilado) de la altura (SectionBars.HorizontalOffset).
        /// </summary>
        private static CornerFace MakeCorner(AppConfig cfg, WallSection s, WallSection o, ZoneLayout otherZones, bool otherU0,
                                             Func<string, double> diameterFt)
        {
            bool otherBack = otherU0 == o.HeelAtU0;
            double sign = otherU0 ? +1 : -1;
            Func<double, double> otherU = v =>
            {
                double db = otherZones.DiameterAt(v, otherBack);
                return (otherU0 ? o.FaceU0(v) : o.FaceU1(v)) + sign * (SectionBars.HorizontalOffset(o, cfg, otherBack, v, db, diameterFt) + db * 0.5);
            };

            // la pata no puede salirse del tramo recto de la otra ala
            double available = Math.Max(0, o.LenW - Mm(cfg.CoverEndMm));
            double lapDia = cfg.CornerLapDiameters, lapMin = Mm(cfg.CornerLapMinMm);

            return new CornerFace
            {
                OtherBarW = v => OtherW(s, o, otherU(v)),
                LegDirU = s.OtherSideU,
                LapFor = db => Math.Min(Math.Max(lapMin, lapDia * db), available)
            };
        }
    }

    /// <summary>Esquina en una cara del alzado: donde acaba el horizontal y hacia donde gira su pata.</summary>
    public sealed class CornerFace
    {
        /// <summary>w (en el marco de este ala) de la linea de barra horizontal de la otra ala a la altura v.</summary>
        public Func<double, double> OtherBarW;
        /// <summary>+1/-1 en u de este ala: hacia el extremo libre de la otra ala.</summary>
        public double LegDirU;
        /// <summary>Longitud de la pata de solape (pies) para una barra de diametro db. 0 = sin pata.</summary>
        public Func<double, double> LapFor;
    }

    /// <summary>
    /// Extension en w de cada familia de barras de un tramo (recubrimiento de extremo ya
    /// aplicado) y detalles de esquina. Para un tramo recto todo va de cov a LenW - cov.
    /// </summary>
    public sealed class WingPlan
    {
        public string Label = "";
        public double VertW0, VertW1;
        public double HorW0, HorW1;
        public double TransW0, TransW1;
        public double LongW0, LongW1;

        /// <summary>Esquina en la cara u=min / u=max del alzado (null = el horizontal acaba recto en HorW1).</summary>
        public CornerFace CornerU0, CornerU1;

        /// <summary>Reparto de horizontales ya resuelto para este tramo (null = resolverlo al armar).</summary>
        public ZoneLayout Zones;

        /// <summary>Sube los horizontales un diametro para que no se crucen con las patas del otro ala.</summary>
        public bool ShiftHorizontals;

        /// <summary>Malla de zapata con las capas intercambiadas (longitudinales fuera, transversales dentro).</summary>
        public bool SwapFootingLayers;
        /// <summary>Diametro de las transversales del otro ala, que quedan debajo/encima al cruzar el bloque.</summary>
        public double OtherTransverseTopDb, OtherTransverseBottomDb;

        public static WingPlan Straight(WallSection s, AppConfig cfg)
        {
            double cov = WallSection.Mm(cfg.CoverEndMm);
            double end = s.LenW - cov;
            return new WingPlan
            {
                Label = "",
                VertW0 = cov, VertW1 = end,
                HorW0 = cov, HorW1 = end,
                TransW0 = cov, TransW1 = end,
                LongW0 = cov, LongW1 = end
            };
        }
    }
}
