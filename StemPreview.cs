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
        private static readonly Brush ReinfBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x3A, 0x8A));

        /// <summary>
        /// Verticales (linea por cara con su patilla), transversales de zapata (linea con
        /// patas) y longitudinales de zapata (puntos). Geometria de SectionBars, la misma
        /// que usa el generador.
        /// </summary>
        private void DrawFixedFamilies(double k, Func<double, double> X, Func<double, double> Y)
        {
            List<Point> ToPts(SectionBars.Poly p)
            {
                var pts = new List<Point>();
                foreach (var q in p.Pts) pts.Add(new Point(X(q.u), Y(q.v)));
                return pts;
            }

            for (int t = 0; t < 2; t++)
            {
                bool top = t == 1;
                BarFamilyCfg tr = top ? _cfg.FootingTransverseTop : _cfg.FootingTransverseBottom;
                SectionBars.Poly p = SectionBars.Transverse(_s, _cfg, top, _diameterFt);
                if (p != null)
                    Children.Add(Bar(ToPts(p), p.Db * k, "Transversal zapata " + (top ? "superior" : "inferior") + ": " + tr.BarTypeName +
                                     " @" + tr.SpacingMm.ToString("0") + " mm, patas " + tr.LegMm.ToString("0") + " mm"));

                BarFamilyCfg lg = top ? _cfg.FootingLongitudinalTop : _cfg.FootingLongitudinalBottom;
                SectionBars.LongitudinalSet l = SectionBars.Longitudinal(_s, _cfg, top, _diameterFt);
                if (l == null || l.Count == 0) continue;
                double r = Math.Max(l.Db * 0.5 * k, 2);
                for (int i = 0; i < l.Count; i++)
                {
                    var dot = new Ellipse
                    {
                        Width = 2 * r, Height = 2 * r, Fill = BarBrush,
                        ToolTip = "Longitudinal zapata " + (top ? "superior" : "inferior") + ": " + lg.BarTypeName +
                                  " @" + lg.SpacingMm.ToString("0") + " mm (" + l.Count + " barras)"
                    };
                    SetLeft(dot, X(l.UAt(i)) - r);
                    SetTop(dot, Y(l.V) - r);
                    Children.Add(dot);
                }
            }

            // refuerzos cortos, en azul oscuro para distinguirlos de la transversal
            if (_cfg.FootingReinforcements != null)
            {
                int i = 0;
                foreach (FootingReinfCfg r in _cfg.FootingReinforcements)
                {
                    i++;
                    SectionBars.Poly p = SectionBars.Reinforcement(_s, _cfg, r, _diameterFt, out _);
                    if (p == null) continue;
                    string len = r.IsCenter
                        ? r.ToeLengthMm.ToString("0") + " hacia puntera + " + r.HeelLengthMm.ToString("0") + " hacia talon desde el eje"
                        : r.LengthMm.ToString("0") + " mm desde el borde";
                    Polyline pl = Bar(ToPts(p), p.Db * k + 1, "Refuerzo zapata " + r.Describe + " #" + i + ": " + r.BarTypeName +
                                      " @" + r.SpacingMm.ToString("0") + " mm, " + len);
                    pl.Stroke = ReinfBrush;
                    Children.Add(pl);
                }
            }

            for (int f = 0; f < 2; f++)
            {
                bool back = f == 0;
                BarFamilyCfg fam = back ? _cfg.StemVerticalBack : _cfg.StemVerticalFront;
                SectionBars.Poly p = SectionBars.Vertical(_s, _cfg, back, _diameterFt);
                if (p == null) continue;
                Children.Add(Bar(ToPts(p), p.Db * k, "Vertical " + (back ? "trasdos" : "intrados") + ": " + fam.BarTypeName +
                                 " @" + fam.SpacingMm.ToString("0") + " mm, patilla " + fam.LegMm.ToString("0") +
                                 " mm mas alla de la cara opuesta" + (fam.CutLengthMm > 0 ? ", baston " + fam.CutLengthMm.ToString("0") + " mm" : "")));
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
