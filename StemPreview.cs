using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace RetainingWallRebar
{
    /// <summary>
    /// Esquema del alzado del muro en seccion con el reparto de horizontales por tramos:
    /// hormigon, bandas de cada tramo, cotas de los limites y cada barra como un punto
    /// (seccion de barra) a su altura y con su diametro. Dibujado con figuras nativas de
    /// WPF sobre un Canvas: el dibujo es pequeno y se redibuja entero en cada cambio.
    /// Las alturas de barra salen de StemZones.Resolve, la misma funcion que usa el
    /// generador, asi que lo que se ve es lo que se crea.
    /// </summary>
    public sealed class StemPreview : Canvas
    {
        private WallSection _s;
        private AppConfig _cfg;
        private ZoneLayout _layout;
        private Func<string, double> _diameterFt;
        private string _message = "Sin elemento armable";
        private int _hoverZone = -1;

        private const double FtToM = 0.3048;

        public static readonly Brush[] ZoneBrushes =
        {
            new SolidColorBrush(Color.FromRgb(0x3B, 0x6F, 0xB6)),
            new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x2E)),
            new SolidColorBrush(Color.FromRgb(0x3F, 0x9C, 0x5A))
        };

        public StemPreview()
        {
            Background = Brushes.White;
            ClipToBounds = true;
            SizeChanged += (s, e) => Redraw();
        }

        /// <summary>Tramo resaltado (indice 0 = inferior) o -1.</summary>
        public int HoverZone
        {
            get => _hoverZone;
            set { if (_hoverZone != value) { _hoverZone = value; Redraw(); } }
        }

        public void Show(WallSection s, AppConfig cfg, ZoneLayout layout, Func<string, double> diameterFt)
        {
            _s = s; _cfg = cfg; _layout = layout; _diameterFt = diameterFt;
            Redraw();
        }

        public void Clear(string message)
        {
            _s = null; _layout = null;
            _message = message;
            Redraw();
        }

        private static string M(double ft) => (ft * FtToM).ToString("0.00", CultureInfo.InvariantCulture);

        private void Redraw()
        {
            Children.Clear();
            double W = ActualWidth, H = ActualHeight;
            if (W < 40 || H < 40) return;

            if (_s == null)
            {
                Children.Add(Label(_message, 8, 8, Brushes.Gray, 12));
                return;
            }

            const double ml = 62, mr = 14, mt = 18, mb = 18;
            double k = Math.Min((W - ml - mr) / _s.LenU, (H - mt - mb) / _s.LenV);
            if (k <= 0 || double.IsInfinity(k)) return;
            // centrar horizontalmente si sobra sitio
            double xOff = ml + Math.Max(0, (W - ml - mr - _s.LenU * k) * 0.5);
            double X(double u) => xOff + u * k;
            double Y(double v) => H - mb - v * k;

            // --- hormigon: zapata + alzado en un solo poligono ---
            var concrete = new Polygon
            {
                Fill = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
                Stroke = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                StrokeThickness = 1
            };
            foreach (var p in new[]
            {
                (0.0, 0.0), (_s.LenU, 0.0), (_s.LenU, _s.FootingTop), (_s.StemU1Bot, _s.FootingTop),
                (_s.StemU1Top, _s.LenV), (_s.StemU0Top, _s.LenV), (_s.StemU0Bot, _s.FootingTop), (0.0, _s.FootingTop)
            })
                concrete.Points.Add(new Point(X(p.Item1), Y(p.Item2)));
            Children.Add(concrete);

            // cara superior de zapata
            Children.Add(Label("zapata", 4, Y(_s.FootingTop) - 8, Brushes.DimGray, 10));

            // --- verticales del alzado y armado de zapata (en gris, como en una seccion) ---
            DrawFixedFamilies(k, X, Y);

            if (_layout == null || _layout.Zones.Count == 0)
            {
                Children.Add(Label(_layout?.Error ?? "sin tramos", 8, 4, Brushes.Firebrick, 11));
                return;
            }

            double dbVBack = _diameterFt(_cfg.StemVerticalBack.BarTypeName);
            double dbVFront = _diameterFt(_cfg.StemVerticalFront.BarTypeName);
            double cover = WallSection.Mm(_cfg.CoverStemMm);
            double stemRight = Math.Max(_s.StemU1Bot, _s.StemU1Top);

            // --- bandas de tramo, limites y barras ---
            foreach (ResolvedZone z in _layout.Zones)
            {
                Brush brush = ZoneBrushes[z.Index % ZoneBrushes.Length];
                var band = new Polygon { Fill = brush, Opacity = z.Index == _hoverZone ? 0.5 : 0.25 };
                band.Points.Add(new Point(X(_s.FaceU0(z.VFrom)), Y(z.VFrom)));
                band.Points.Add(new Point(X(_s.FaceU1(z.VFrom)), Y(z.VFrom)));
                band.Points.Add(new Point(X(_s.FaceU1(z.VTo)), Y(z.VTo)));
                band.Points.Add(new Point(X(_s.FaceU0(z.VTo)), Y(z.VTo)));
                Children.Add(band);

                if (!z.IsTop)
                {
                    var line = new Line
                    {
                        X1 = X(_s.FaceU0(z.VTo)) - 30, X2 = X(_s.FaceU1(z.VTo)) + 8,
                        Y1 = Y(z.VTo), Y2 = Y(z.VTo),
                        Stroke = Brushes.Black, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 }
                    };
                    Children.Add(line);
                    Children.Add(Label("+" + M(z.VTo - _s.FootingTop) + " m", 4, Y(z.VTo) - 16, Brushes.Black, 10, true));
                }

                // etiqueta del tramo a la derecha del alzado
                double vMid = (z.VFrom + z.VTo) * 0.5;
                string txt = "Tramo " + (z.Index + 1);
                if (z.Back != null) txt += "\ntrasdos " + z.Back.BarTypeName + " @" + WallSection.ToMm(z.Back.Spacing) + " (" + z.Back.Heights.Count + ")";
                if (z.Front != null)
                    txt += z.Cfg.SameBothFaces && z.Back != null
                        ? "\nintrados igual (" + z.Front.Heights.Count + ")"
                        : "\nintrados " + z.Front.BarTypeName + " @" + WallSection.ToMm(z.Front.Spacing) + " (" + z.Front.Heights.Count + ")";
                double lx = X(stemRight) + 14;
                if (lx > W - 110) lx = W - 110;
                Children.Add(Label(txt, lx, Y(vMid) - 18, brush, 10, z.Index == _hoverZone));

                // barras: trasdos en la cara del talon
                for (int f = 0; f < 2; f++)
                {
                    bool back = f == 0;
                    ResolvedFace rf = z.Face(back);
                    if (rf == null) continue;
                    bool useU0 = back ? _s.HeelAtU0 : !_s.HeelAtU0;
                    double sign = useU0 ? +1 : -1;
                    double dbV = back ? dbVBack : dbVFront;
                    double r = Math.Max(rf.Db * 0.5 * k, 2.5);
                    foreach (double v in rf.Heights)
                    {
                        double u = (useU0 ? _s.FaceU0(v) : _s.FaceU1(v)) + sign * (cover + dbV + rf.Db * 0.5);
                        var dot = new Ellipse
                        {
                            Width = 2 * r, Height = 2 * r,
                            Fill = brush, Stroke = Brushes.Black, StrokeThickness = 0.6,
                            ToolTip = "Tramo " + (z.Index + 1) + " " + (back ? "trasdos" : "intrados") + ": " + rf.BarTypeName +
                                      " @" + WallSection.ToMm(rf.Spacing) + " mm, cota +" + M(v - _s.FootingTop) + " m"
                        };
                        SetLeft(dot, X(u) - r);
                        SetTop(dot, Y(v) - r);
                        Children.Add(dot);
                    }
                }
            }

            // orientacion
            double uBack = _s.HeelAtU0 ? _s.StemU0Top : _s.StemU1Top;
            Children.Add(Label("trasdos", X(uBack) + (_s.HeelAtU0 ? -46 : 6), Y(_s.LenV) - 14, Brushes.DimGray, 10));
            Children.Add(Label("H alzado " + M(_s.LenV - _s.FootingTop) + " m", W - 96, 2, Brushes.DimGray, 10));

            if (_layout.Error != null)
                Children.Add(Label(_layout.Error, 8, 4, Brushes.Firebrick, 11));
        }

        private static readonly Brush BarBrush = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50));

        /// <summary>
        /// Verticales (linea por cara con su patilla), transversales de zapata (linea con
        /// patas) y longitudinales de zapata (puntos). Misma geometria que RebarGenerator.
        /// </summary>
        private void DrawFixedFamilies(double k, Func<double, double> X, Func<double, double> Y)
        {
            double covSide = WallSection.Mm(_cfg.CoverFootingSideMm);
            double covBot = WallSection.Mm(_cfg.CoverFootingBottomMm);
            double covTop = WallSection.Mm(_cfg.CoverFootingTopMm);
            double covStem = WallSection.Mm(_cfg.CoverStemMm);
            double covCrown = WallSection.Mm(_cfg.CoverStemTopMm);

            // verticales
            for (int f = 0; f < 2; f++)
            {
                bool back = f == 0;
                BarFamilyCfg fam = back ? _cfg.StemVerticalBack : _cfg.StemVerticalFront;
                if (!fam.Enabled) continue;
                double db = _diameterFt(fam.BarTypeName);
                bool useU0 = back ? _s.HeelAtU0 : !_s.HeelAtU0;
                double sign = useU0 ? +1 : -1;
                double uAt(double v) => (useU0 ? _s.FaceU0(v) : _s.FaceU1(v)) + sign * (covStem + db * 0.5);
                double vTop = _s.LenV - covCrown - db * 0.5;
                double vBot = covBot + db * 0.5;
                if (fam.CutLengthMm > 0) vTop = Math.Min(vTop, _s.FootingTop + WallSection.Mm(fam.CutLengthMm));
                double uEnd = uAt(vBot) - sign * WallSection.Mm(fam.LegMm);
                uEnd = Math.Min(Math.Max(uEnd, covSide + db), _s.LenU - covSide - db);
                var pts = new List<Point> { new Point(X(uAt(vTop)), Y(vTop)), new Point(X(uAt(vBot)), Y(vBot)), new Point(X(uEnd), Y(vBot)) };
                Children.Add(Bar(pts, db * k, "Vertical " + (back ? "trasdos" : "intrados") + ": " + fam.BarTypeName +
                                 " @" + fam.SpacingMm.ToString("0") + " mm, patilla " + fam.LegMm.ToString("0") + " mm"));
            }

            // transversales y longitudinales de zapata
            for (int t = 0; t < 2; t++)
            {
                bool top = t == 1;
                BarFamilyCfg tr = top ? _cfg.FootingTransverseTop : _cfg.FootingTransverseBottom;
                BarFamilyCfg lg = top ? _cfg.FootingLongitudinalTop : _cfg.FootingLongitudinalBottom;
                double dbT = _diameterFt(tr.BarTypeName);
                if (tr.Enabled)
                {
                    double v = top ? _s.FootingTop - covTop - dbT * 0.5 : covBot + dbT * 0.5;
                    double ua = covSide + dbT * 0.5, ub = _s.LenU - ua;
                    double vLeg = v + (top ? -1 : +1) * WallSection.Mm(tr.LegMm);
                    vLeg = Math.Min(Math.Max(vLeg, covBot), _s.FootingTop - covTop);
                    var pts = new List<Point>();
                    if (tr.LegMm > 0) pts.Add(new Point(X(ua), Y(vLeg)));
                    pts.Add(new Point(X(ua), Y(v)));
                    pts.Add(new Point(X(ub), Y(v)));
                    if (tr.LegMm > 0) pts.Add(new Point(X(ub), Y(vLeg)));
                    Children.Add(Bar(pts, dbT * k, "Transversal zapata " + (top ? "superior" : "inferior") + ": " + tr.BarTypeName +
                                     " @" + tr.SpacingMm.ToString("0") + " mm"));
                }
                if (lg.Enabled)
                {
                    double db = _diameterFt(lg.BarTypeName);
                    double v = top ? _s.FootingTop - covTop - dbT - db * 0.5 : covBot + dbT + db * 0.5;
                    double u0 = covSide + db * 0.5;
                    double span = _s.LenU - 2 * u0;
                    double spacing = WallSection.Mm(lg.SpacingMm);
                    if (span <= 0 || spacing <= 0) continue;
                    int n = (int)Math.Ceiling(span / spacing - 1e-9) + 1;
                    double r = Math.Max(db * 0.5 * k, 2);
                    for (int i = 0; i < n; i++)
                    {
                        double u = u0 + (n > 1 ? span * i / (n - 1) : 0);
                        var dot = new Ellipse
                        {
                            Width = 2 * r, Height = 2 * r, Fill = BarBrush,
                            ToolTip = "Longitudinal zapata " + (top ? "superior" : "inferior") + ": " + lg.BarTypeName +
                                      " @" + lg.SpacingMm.ToString("0") + " mm (" + n + " barras)"
                        };
                        SetLeft(dot, X(u) - r);
                        SetTop(dot, Y(v) - r);
                        Children.Add(dot);
                    }
                }
            }
        }

        private static Polyline Bar(List<Point> pts, double thicknessPx, string tip)
        {
            var pl = new Polyline
            {
                Stroke = BarBrush,
                StrokeThickness = Math.Max(thicknessPx, 1.5),
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                ToolTip = tip
            };
            foreach (Point p in pts) pl.Points.Add(p);
            return pl;
        }

        private static TextBlock Label(string text, double x, double y, Brush brush, double size, bool bold = false)
        {
            var tb = new TextBlock
            {
                Text = text,
                Foreground = brush,
                FontSize = size,
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                Background = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF))
            };
            SetLeft(tb, x);
            SetTop(tb, y);
            return tb;
        }
    }
}
