using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace RetainingWallRebar
{
    /// <summary>
    /// Esquema de la seccion del muro con todo el armado: hormigon, tramos de horizontales
    /// (bandas de color y cada barra como un punto, incluidas las que bajan a la zapata),
    /// verticales con sus patillas, bastones (a trazos si van intercalados en otro plano),
    /// transversales de zapata con sus patas, longitudinales y refuerzos. Cada tipo de
    /// elemento tiene su color (ver Kinds) y se puede resaltar desde la leyenda o desde las
    /// filas de tramos. Zoom con la rueda (centrado en el cursor), desplazamiento
    /// arrastrando y doble clic para volver a encajar la seccion.
    /// Dibujado con figuras nativas de WPF sobre un Canvas: el dibujo es pequeno y se
    /// redibuja entero en cada cambio. Toda la geometria sale de StemZones y SectionBars,
    /// las mismas funciones que usa el generador.
    /// </summary>
    public sealed class StemPreview : Canvas
    {
        private WallSection _s;
        private AppConfig _cfg;
        private ZoneLayout _layout;
        private Func<string, double> _diameterFt;
        private string _message = "Sin elemento armable";

        /// <summary>Elemento resaltado al pasar el raton: "zone:N", o una de las claves de Kinds; null = ninguno.</summary>
        private string _highlight;

        /// <summary>Datos de una barra dibujada, para seleccionarla con un clic.</summary>
        private sealed class BarInfo
        {
            public string Key;      // clave de resaltado (familia o "zone:N")
            public string Label;    // texto de la etiqueta
            public double U, V;     // punto de anclaje (coordenadas del muro)
        }

        private readonly List<BarInfo> _infos = new List<BarInfo>();
        private BarInfo _selected;
        private const double ClickTol = 4;

        /// <summary>Clave activa: la del raton si esta sobre algo, si no la de la barra seleccionada.</summary>
        private string ActiveKey => _highlight ?? _selected?.Key;

        private double _zoom = 1;
        private Vector _pan;
        /// <summary>Origen del dibujo sin zoom ni desplazamiento (se fija en Redraw, se usa al hacer zoom).</summary>
        private double _x0, _y0;
        private Point _dragStart;
        private Vector _panStart;
        private bool _dragging;

        private const double FtToM = 0.3048;
        private const double LabelColumn = 118;

        public static readonly Brush[] ZoneBrushes =
        {
            new SolidColorBrush(Color.FromRgb(0x3B, 0x6F, 0xB6)),
            new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x2E)),
            new SolidColorBrush(Color.FromRgb(0x3F, 0x9C, 0x5A))
        };

        /// <summary>Tipos de elemento (clave, nombre en la leyenda, color).</summary>
        public static readonly (string key, string name, Brush brush)[] Kinds =
        {
            ("vertical", "Verticales del alzado", new SolidColorBrush(Color.FromRgb(0x8B, 0x2E, 0x2E))),
            ("dowel", "Bastones de arranque", new SolidColorBrush(Color.FromRgb(0x7A, 0x3E, 0x9D))),
            ("transverse", "Transversales de zapata", new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50))),
            ("longitudinal", "Longitudinales de zapata", new SolidColorBrush(Color.FromRgb(0x1F, 0x7A, 0x7A))),
            ("reinf", "Refuerzos de zapata", new SolidColorBrush(Color.FromRgb(0x1F, 0x3A, 0x8A)))
        };

        public static Brush KindBrush(string key)
        {
            foreach (var k in Kinds) if (k.key == key) return k.brush;
            return Brushes.Black;
        }

        public StemPreview()
        {
            Background = Brushes.White;
            ClipToBounds = true;
            SizeChanged += (s, e) => Redraw();

            MouseWheel += OnWheel;
            MouseLeftButtonDown += OnDown;
            MouseMove += OnMove;
            MouseLeftButtonUp += OnUp;
            MouseLeave += (s, e) => { _dragging = false; ReleaseMouseCapture(); };
            Cursor = Cursors.Hand;
        }

        /// <summary>Elemento resaltado ("zone:N" o clave de Kinds); null = ninguno.</summary>
        public string Highlight
        {
            get => _highlight;
            set { if (_highlight != value) { _highlight = value; Redraw(); } }
        }

        /// <summary>Compatibilidad con las filas de tramos: indice 0 = inferior, -1 = ninguno.</summary>
        public int HoverZone
        {
            set => Highlight = value < 0 ? null : "zone:" + value;
        }

        public void Show(WallSection s, AppConfig cfg, ZoneLayout layout, Func<string, double> diameterFt)
        {
            bool newWall = !ReferenceEquals(_s, s);
            _s = s; _cfg = cfg; _layout = layout; _diameterFt = diameterFt;
            if (newWall) { _selected = null; ResetView(); }
            else Redraw();
        }

        public void Clear(string message)
        {
            _s = null; _layout = null; _selected = null;
            _message = message;
            Redraw();
        }

        public void ResetView()
        {
            _zoom = 1;
            _pan = new Vector(0, 0);
            Redraw();
        }

        // ------------------------------------------------------------------
        // Zoom y desplazamiento
        // ------------------------------------------------------------------
        private void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (_s == null) return;
            double factor = e.Delta > 0 ? 1.25 : 1 / 1.25;
            double newZoom = Math.Max(1, Math.Min(40, _zoom * factor));
            factor = newZoom / _zoom;
            // el punto bajo el cursor se queda quieto: X = pan + x0 + u*k
            Point m = e.GetPosition(this);
            _pan = new Vector(m.X - _x0 - (m.X - _pan.X - _x0) * factor,
                              m.Y - _y0 - (m.Y - _pan.Y - _y0) * factor);
            _zoom = newZoom;
            if (_zoom <= 1.0001) _pan = new Vector(0, 0);
            Redraw();
            e.Handled = true;   // que no desplace el ScrollViewer de la ventana
        }

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            if (_s == null) return;
            if (e.ClickCount == 2) { ResetView(); e.Handled = true; return; }   // doble clic: encajar
            _dragging = true;
            _dragStart = e.GetPosition(this);
            _panStart = _pan;
            CaptureMouse();
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            Point m = e.GetPosition(this);
            _pan = _panStart + (m - _dragStart);
            Redraw();
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            bool wasDragging = _dragging;
            _dragging = false;
            ReleaseMouseCapture();
            if (!wasDragging || _s == null) return;
            Point m = e.GetPosition(this);
            if ((m - _dragStart).Length > ClickTol) return;   // fue un arrastre, no un clic

            // clic: la barra bajo el cursor (o nada, que quita la seleccion)
            BarInfo hit = null;
            VisualTreeHelper.HitTest(this, null, r =>
            {
                for (DependencyObject d = r.VisualHit; d != null && d != this; d = VisualTreeHelper.GetParent(d))
                    if (d is FrameworkElement fe && fe.Tag is BarInfo info) { hit = info; return HitTestResultBehavior.Stop; }
                return HitTestResultBehavior.Continue;
            }, new PointHitTestParameters(m));
            _selected = hit;
            Redraw();
        }

        // ------------------------------------------------------------------
        // Dibujo
        // ------------------------------------------------------------------
        private static string M(double ft) => (ft * FtToM).ToString("0.00", CultureInfo.InvariantCulture);

        private bool Dimmed(string key) => ActiveKey != null && ActiveKey != key;
        private bool Lit(string key) => ActiveKey == key;

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

            // escala base: la seccion entera encaja dejando sitio a la columna de etiquetas
            const double ml = 62, mt = 22, mb = 18;
            double mr = LabelColumn + 8;
            double kBase = Math.Min((W - ml - mr) / _s.LenU, (H - mt - mb) / _s.LenV);
            if (kBase <= 0 || double.IsInfinity(kBase)) return;
            double k = kBase * _zoom;
            _x0 = ml + Math.Max(0, (W - ml - mr - _s.LenU * kBase) * 0.5);
            _y0 = H - mb;
            double X(double u) => _pan.X + _x0 + u * k;
            double Y(double v) => _pan.Y + _y0 - v * k;

            _infos.Clear();
            DrawConcrete(X, Y);
            DrawFixedFamilies(k, X, Y);
            DrawZones(k, W, X, Y);
            DrawSelection(W, H, X, Y);

            // textos fijos
            Children.Add(Label("zapata", 4, Y(_s.FootingTop) - 8, Brushes.DimGray, 10));
            double uBack = _s.HeelAtU0 ? _s.StemU0Top : _s.StemU1Top;
            double uFront = _s.HeelAtU0 ? _s.StemU1Top : _s.StemU0Top;
            Children.Add(Label("trasdos", X(uBack) + (_s.HeelAtU0 ? -46 : 6), Y(_s.LenV) - 14, Brushes.DimGray, 10));
            Children.Add(Label("intrados", X(uFront) + (_s.HeelAtU0 ? 6 : -50), Y(_s.LenV) - 14, Brushes.DimGray, 10));
            Children.Add(Label("H alzado " + M(_s.LenV - _s.FootingTop) + " m" +
                               (_zoom > 1.0001 ? "   zoom x" + _zoom.ToString("0.0", CultureInfo.InvariantCulture) + " (doble clic: encajar)" : "   rueda: zoom · arrastrar: mover · clic en una barra: datos"),
                               4, 2, Brushes.DimGray, 10));
            if (_layout != null && _layout.Error != null)
                Children.Add(Label(_layout.Error, 8, 16, Brushes.Firebrick, 11));
        }

        /// <summary>
        /// Etiqueta de la barra seleccionada. La seleccion se guarda por clave y punto de
        /// anclaje: tras cada redibujo se vuelve a buscar la barra equivalente, asi el texto
        /// refleja los valores actuales (o desaparece si esa barra ya no existe).
        /// </summary>
        private void DrawSelection(double W, double H, Func<double, double> X, Func<double, double> Y)
        {
            if (_selected == null) return;
            BarInfo best = null;
            double bestD = double.MaxValue;
            foreach (BarInfo i in _infos)
            {
                if (i.Key != _selected.Key) continue;
                double d = Math.Abs(i.U - _selected.U) + Math.Abs(i.V - _selected.V);
                if (d < bestD) { bestD = d; best = i; }
            }
            if (best == null || bestD > WallSection.Mm(150)) { _selected = null; return; }
            _selected = best;

            Brush brush = best.Key.StartsWith("zone:")
                ? ZoneBrushes[int.Parse(best.Key.Substring(5)) % ZoneBrushes.Length]
                : KindBrush(best.Key);
            double ax = X(best.U), ay = Y(best.V);

            var ring = new Ellipse { Width = 14, Height = 14, Stroke = brush, StrokeThickness = 2, Fill = Brushes.Transparent, IsHitTestVisible = false };
            SetLeft(ring, ax - 7); SetTop(ring, ay - 7);
            Children.Add(ring);

            var text = new TextBlock { Text = best.Label, FontSize = 11, Foreground = Brushes.Black, TextWrapping = TextWrapping.Wrap, MaxWidth = 230 };
            var box = new Border
            {
                Child = text, Background = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)),
                BorderBrush = brush, BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 4, 6, 4), IsHitTestVisible = false
            };
            box.Measure(new Size(240, 200));
            double bw = box.DesiredSize.Width, bh = box.DesiredSize.Height;
            double bx = ax + 14, by = ay - bh - 6;
            if (bx + bw > W - 4) bx = ax - bw - 14;
            if (bx < 4) bx = 4;
            if (by < 4) by = ay + 14;
            if (by + bh > H - 4) by = H - 4 - bh;
            SetLeft(box, bx); SetTop(box, by);
            Children.Add(box);
            Children.Add(new Line { X1 = ax, Y1 = ay, X2 = bx + (bx > ax ? 0 : bw), Y2 = by + bh * 0.5, Stroke = brush, StrokeThickness = 1, IsHitTestVisible = false });
        }

        private BarInfo Info(string key, string label, double u, double v)
        {
            var i = new BarInfo { Key = key, Label = label, U = u, V = v };
            _infos.Add(i);
            return i;
        }

        private void DrawConcrete(Func<double, double> X, Func<double, double> Y)
        {
            var concrete = new Polygon
            {
                Fill = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE4)),
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
        }

        private void DrawZones(double k, double W, Func<double, double> X, Func<double, double> Y)
        {
            if (_layout == null || _layout.Zones.Count == 0) return;

            double labelX = W - LabelColumn;

            foreach (ResolvedZone z in _layout.Zones)
            {
                string key = "zone:" + z.Index;
                Brush brush = ZoneBrushes[z.Index % ZoneBrushes.Length];
                double op = Lit(key) ? 0.5 : Dimmed(key) ? 0.12 : 0.25;
                bool intoFooting = z.VLow < z.VFrom - 1e-9;
                string zoneLabel = "Tramo " + (z.Index + 1) + " (de +" + M(z.VFrom - _s.FootingTop) + " a +" + M(z.VTo - _s.FootingTop) + " m" +
                                   (intoFooting ? ", baja " + M(z.VFrom - z.VLow) + " m en la zapata)" : ")");
                var band = new Polygon { Fill = brush, Opacity = op, Tag = Info(key, zoneLabel + FaceLines(z), (_s.FaceU0((z.VFrom + z.VTo) * 0.5) + _s.FaceU1((z.VFrom + z.VTo) * 0.5)) * 0.5, (z.VFrom + z.VTo) * 0.5) };
                // la banda del tramo inferior sigue las caras (prolongadas) hasta su ultima barra en la zapata
                band.Points.Add(new Point(X(_s.FaceU0(z.VLow)), Y(z.VLow)));
                band.Points.Add(new Point(X(_s.FaceU1(z.VLow)), Y(z.VLow)));
                band.Points.Add(new Point(X(_s.FaceU1(z.VTo)), Y(z.VTo)));
                band.Points.Add(new Point(X(_s.FaceU0(z.VTo)), Y(z.VTo)));
                Children.Add(band);

                if (!z.IsTop)
                {
                    Children.Add(new Line
                    {
                        X1 = X(_s.FaceU0(z.VTo)) - 30, X2 = X(_s.FaceU1(z.VTo)) + 8,
                        Y1 = Y(z.VTo), Y2 = Y(z.VTo),
                        Stroke = Brushes.Black, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 }
                    });
                    Children.Add(Label("+" + M(z.VTo - _s.FootingTop) + " m", 4, Y(z.VTo) - 16, Brushes.Black, 10, true));
                }

                // etiqueta en la columna de la derecha, con linea de referencia a la banda
                double vMid = (z.VFrom + z.VTo) * 0.5;
                string txt = "Tramo " + (z.Index + 1) + FaceLines(z);
                double ly = Math.Max(2, Math.Min(ActualHeight - 44, Y(vMid) - 18));
                Children.Add(new Line
                {
                    X1 = X(Math.Max(_s.FaceU1(z.VFrom), _s.FaceU1(z.VTo))) + 2, Y1 = Y(vMid),
                    X2 = labelX - 4, Y2 = ly + 18,
                    Stroke = brush, StrokeThickness = 1, Opacity = Dimmed(key) ? 0.3 : 0.8
                });
                TextBlock tb = Label(txt, labelX, ly, brush, 10, Lit(key));
                tb.Opacity = Dimmed(key) ? 0.4 : 1;
                Children.Add(tb);

                // barras: trasdos en la cara del talon
                for (int f = 0; f < 2; f++)
                {
                    bool back = f == 0;
                    ResolvedFace rf = z.Face(back);
                    if (rf == null) continue;
                    bool useU0 = back ? _s.HeelAtU0 : !_s.HeelAtU0;
                    double sign = useU0 ? +1 : -1;
                    double r = Math.Max(rf.Db * 0.5 * k, 2.5);
                    foreach (double v in rf.Heights)
                    {
                        double u = (useU0 ? _s.FaceU0(v) : _s.FaceU1(v)) + sign * (SectionBars.HorizontalOffset(_s, _cfg, back, v, rf.Db, _diameterFt) + rf.Db * 0.5);
                        string tip = "Horizontal " + (back ? "trasdos" : "intrados") + " · Tramo " + (z.Index + 1) + "\n" + rf.BarTypeName +
                                     " @" + WallSection.ToMm(rf.Spacing) + " mm max., real " + WallSection.ToMm(rf.RealSpacing) + " mm · " +
                                     rf.Heights.Count + " barras en el tramo" +
                                     (rf.FootingCount > 0 ? " (" + rf.FootingCount + " en la zapata)" : "") +
                                     "\ncota " + (v < _s.FootingTop ? "-" : "+") + M(Math.Abs(v - _s.FootingTop)) + " m";
                        var dot = new Ellipse
                        {
                            Width = 2 * r, Height = 2 * r,
                            Fill = brush, Stroke = Brushes.Black, StrokeThickness = 0.6,
                            Opacity = Dimmed(key) ? 0.3 : 1,
                            ToolTip = tip,
                            Tag = Info(key, tip, u, v)
                        };
                        SetLeft(dot, X(u) - r);
                        SetTop(dot, Y(v) - r);
                        Children.Add(dot);
                    }
                }
            }
        }

        private static string FaceLines(ResolvedZone z)
        {
            string t = "";
            if (z.Back != null) t += "\ntrasdos " + z.Back.BarTypeName + " @" + WallSection.ToMm(z.Back.RealSpacing) + " (" + Count(z.Back) + ")";
            if (z.Front != null) t += "\nintrados " + z.Front.BarTypeName + " @" + WallSection.ToMm(z.Front.RealSpacing) + " (" + Count(z.Front) + ")";
            return t;
        }

        private static string Count(ResolvedFace f) =>
            f.FootingCount > 0 ? f.Heights.Count + ", " + f.FootingCount + " en zapata" : f.Heights.Count.ToString();

        private static string Shifted(double shiftFt) =>
            shiftFt > 1e-6 ? "\napartada " + WallSection.ToMm(shiftFt) + " mm hacia dentro por un refuerzo" : "";

        /// <summary>
        /// Verticales y bastones (linea por cara con su patilla), transversales de zapata
        /// (linea con patas), longitudinales (puntos) y refuerzos (linea). Geometria de
        /// SectionBars, la misma que usa el generador.
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
                {
                    SectionBars.FootingLayer layer = SectionBars.Layer(_s, _cfg, top, _diameterFt);
                    string tipT = "Transversal zapata " + (top ? "superior" : "inferior") + "\n" + tr.BarTypeName +
                                  " @" + tr.SpacingMm.ToString("0") + " mm · patas " + tr.LegMm.ToString("0") + " mm" + Shifted(layer.TransShift);
                    (double u, double v) mid = p.Pts[p.Pts.Count / 2];
                    Children.Add(Bar(ToPts(p), p.Db * k, "transverse", tipT, Info("transverse", tipT, (p.Pts[1].u + p.Pts[p.Pts.Count - 2].u) * 0.5, mid.v)));
                }

                BarFamilyCfg lg = top ? _cfg.FootingLongitudinalTop : _cfg.FootingLongitudinalBottom;
                SectionBars.LongitudinalSet l = SectionBars.Longitudinal(_s, _cfg, top, _diameterFt);
                if (l == null || l.Count == 0) continue;
                double r = Math.Max(l.Db * 0.5 * k, 2);
                string tipL = "Longitudinal zapata " + (top ? "superior" : "inferior") + "\n" + lg.BarTypeName +
                              " @" + lg.SpacingMm.ToString("0") + " mm · " + l.Count + " barras" + Shifted(l.Shift);
                for (int i = 0; i < l.Count; i++)
                {
                    var dot = new Ellipse
                    {
                        Width = 2 * r, Height = 2 * r, Fill = KindBrush("longitudinal"),
                        Opacity = Dimmed("longitudinal") ? 0.3 : 1,
                        ToolTip = tipL,
                        Tag = Info("longitudinal", tipL, l.UAt(i), l.V)
                    };
                    SetLeft(dot, X(l.UAt(i)) - r);
                    SetTop(dot, Y(l.V) - r);
                    Children.Add(dot);
                }
            }

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
                    string tipR = "Refuerzo zapata " + r.Describe + " #" + i + "\n" + r.BarTypeName + " @" + r.SpacingMm.ToString("0") +
                                  " mm\n" + len + "\n" + (r.Above ? "encima" : "debajo") + " de la transversal, hueco " + r.GapMm.ToString("0") + " mm";
                    Children.Add(Bar(ToPts(p), p.Db * k + 1, "reinf", tipR, Info("reinf", tipR, (p.Pts[0].u + p.Pts[1].u) * 0.5, p.Pts[0].v)));
                }
            }

            for (int d = 0; d < 2; d++)
            {
                bool dowel = d == 1;
                for (int f = 0; f < 2; f++)
                {
                    bool back = f == 0;
                    BarFamilyCfg fam = dowel ? (back ? _cfg.StemDowelBack : _cfg.StemDowelFront)
                                             : (back ? _cfg.StemVerticalBack : _cfg.StemVerticalFront);
                    SectionBars.Poly p = dowel ? SectionBars.Dowel(_s, _cfg, back, _diameterFt, out _) : SectionBars.Vertical(_s, _cfg, back, _diameterFt);
                    if (p == null) continue;
                    string tip = (dowel ? "Baston " : "Vertical ") + (back ? "trasdos" : "intrados") + "\n" + fam.BarTypeName +
                                 " @" + fam.SpacingMm.ToString("0") + " mm\n" +
                                 (dowel ? "anclaje " + fam.EmbedMm.ToString("0") + " mm en zapata · altura " + fam.CutLengthMm.ToString("0") + " mm sobre la zapata\n" +
                                          (fam.Stacked ? "apilado por dentro de la vertical, hueco " + fam.GapMm.ToString("0") + " mm" : "intercalado media separacion con las verticales (otro plano)")
                                        : "patilla " + fam.LegMm.ToString("0") + " mm mas alla de la cara opuesta" +
                                          (fam.CrownLegMm > 0 ? "\npatilla de coronacion " + fam.CrownLegMm.ToString("0") + " mm hacia la cara contraria" : "\nsin patilla de coronacion"));
                    // el punto de anclaje de la etiqueta va a media altura del tramo vertical
                    int a = 0;
                    for (int i = 0; i + 1 < p.Pts.Count; i++)
                        if (Math.Abs(p.Pts[i + 1].v - p.Pts[i].v) > Math.Abs(p.Pts[a + 1].v - p.Pts[a].v)) a = i;
                    (double u, double v) mid = ((p.Pts[a].u + p.Pts[a + 1].u) * 0.5, (p.Pts[a].v + p.Pts[a + 1].v) * 0.5);
                    Children.Add(Bar(ToPts(p), p.Db * k, dowel ? "dowel" : "vertical", tip, Info(dowel ? "dowel" : "vertical", tip, mid.u, mid.v), dowel && !fam.Stacked));
                }
            }
        }

        private Polyline Bar(List<Point> pts, double thicknessPx, string kind, string tip, BarInfo info, bool dashed = false)
        {
            var pl = new Polyline
            {
                Stroke = KindBrush(kind),
                StrokeThickness = Math.Max(thicknessPx, 2) + (Lit(kind) ? 1.5 : 0),
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = dashed ? PenLineCap.Flat : PenLineCap.Round,
                StrokeEndLineCap = dashed ? PenLineCap.Flat : PenLineCap.Round,
                Opacity = Dimmed(kind) ? 0.3 : 1,
                ToolTip = tip,
                Tag = info
            };
            // a trazos = la barra va en otro plano (baston intercalado entre las verticales)
            if (dashed) pl.StrokeDashArray = new DoubleCollection { 2.5, 1.5 };
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
