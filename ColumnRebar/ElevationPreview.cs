using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ColumnRebar
{
    /// <summary>
    /// Esquema del alzado: la columna con sus longitudinales (con las prolongaciones por
    /// encima y por debajo a trazos) y una raya por cada estribo, con la etiqueta de cada
    /// tramo de la distribucion ("1@50", "5@100", "resto R@250 (=237)").
    /// </summary>
    public sealed class ElevationPreview : Canvas
    {
        private ColumnSection _s;
        private ColumnPlan _plan;
        private List<StirrupRun> _runs;
        private AppConfig _cfg;
        private string _message = "Sin elemento armable";
        private const double FtToMm = 304.8;

        public ElevationPreview()
        {
            Background = Brushes.White;
            ClipToBounds = true;
            SizeChanged += (s, e) => Redraw();
        }

        public void Show(ColumnSection s, ColumnPlan plan, List<StirrupRun> runs, AppConfig cfg)
        {
            _s = s; _plan = plan; _runs = runs; _cfg = cfg;
            Redraw();
        }

        public void Clear(string message)
        {
            _s = null; _runs = null; _message = message;
            Redraw();
        }

        private static string Mm(double ft) => Math.Round(ft * FtToMm).ToString(CultureInfo.InvariantCulture);

        private void Redraw()
        {
            Children.Clear();
            double W = ActualWidth, H = ActualHeight;
            if (W < 10 || H < 10) return;
            if (_s == null || _runs == null || _cfg == null) { Text(_message, 10, 10, Brushes.Gray, 12); return; }

            double ext0 = Math.Max(0, _cfg.Longitudinal.BottomExtensionMm) / FtToMm;
            double ext1 = Math.Max(0, _cfg.Longitudinal.TopExtensionMm) / FtToMm;
            double total = _s.Height + ext0 + ext1;
            double margin = 24;
            double labelW = 150;
            double k = Math.Min((H - 2 * margin) / Math.Max(total, 1e-6), (W - labelW - 2 * margin) / Math.Max(_s.Width, 1e-6) * 3);
            double colW = Math.Max(20, Math.Min(_s.Width * k, W - labelW - 2 * margin));
            double x0 = margin + 20;
            double yBase = H - margin - ext0 * k;   // cota de la base
            Func<double, double> Y = z => yBase - z * k;

            // hormigon
            var col = new Rectangle
            {
                Width = colW, Height = _s.Height * k,
                Fill = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)), Stroke = Brushes.DimGray, StrokeThickness = 1.2
            };
            SetLeft(col, x0); SetTop(col, Y(_s.Height));
            Children.Add(col);
            Text("base", x0 - 32, Y(0) - 7, Brushes.DimGray, 10);
            Text((_s.Height * 0.3048).ToString("0.00", CultureInfo.InvariantCulture) + " m", x0 - 40, Y(_s.Height) - 7, Brushes.DimGray, 10);

            // longitudinales: la primera y la ultima barra en u
            if (_plan != null && _plan.Error == null && _plan.Bars.Count > 0)
            {
                double uMin = _plan.Bars.Min(b => b.P.U), uMax = _plan.Bars.Max(b => b.P.U);
                double scale = colW / Math.Max(_s.Width, 1e-6);
                foreach (double u in new[] { uMin, uMax })
                {
                    double x = x0 + u * scale;
                    Children.Add(new Line { X1 = x, Y1 = Y(0), X2 = x, Y2 = Y(_s.Height), Stroke = SectionPreview.RequiredBrush, StrokeThickness = 2 });
                    if (ext0 > 0)
                        Children.Add(new Line { X1 = x, Y1 = Y(0), X2 = x, Y2 = Y(-ext0), Stroke = SectionPreview.RequiredBrush, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 3, 2 } });
                    if (ext1 > 0)
                        Children.Add(new Line { X1 = x, Y1 = Y(_s.Height), X2 = x, Y2 = Y(_s.Height + ext1), Stroke = SectionPreview.RequiredBrush, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 3, 2 } });
                }
                if (ext0 > 0) Text("-" + Mm(ext0) + " mm", x0 + colW + 4, Y(-ext0) - 6, SectionPreview.RequiredBrush, 10);
                if (ext1 > 0) Text("+" + Mm(ext1) + " mm", x0 + colW + 4, Y(_s.Height + ext1) - 6, SectionPreview.RequiredBrush, 10);
            }

            // estribos
            Brush sb = SectionPreview.StirrupBrush(0);
            foreach (StirrupRun run in _runs)
            {
                foreach (double z in run.Stations())
                    Children.Add(new Line { X1 = x0 + 3, Y1 = Y(z), X2 = x0 + colW - 3, Y2 = Y(z), Stroke = sb, StrokeThickness = 1.4 });
                double zMid = run.Z0 + 0.5 * run.Length;
                Text(run.Label + (run.Count > 1 ? "  x" + run.Count : ""), x0 + colW + 40, Y(zMid) - 7, sb, 10);
                // llave del tramo
                double yA = Y(run.Z0 + run.Length), yB = Y(run.Z0);
                Children.Add(new Line { X1 = x0 + colW + 32, Y1 = yA, X2 = x0 + colW + 32, Y2 = yB, Stroke = sb, StrokeThickness = 1 });
                Children.Add(new Line { X1 = x0 + colW + 28, Y1 = yA, X2 = x0 + colW + 32, Y2 = yA, Stroke = sb, StrokeThickness = 1 });
                Children.Add(new Line { X1 = x0 + colW + 28, Y1 = yB, X2 = x0 + colW + 32, Y2 = yB, Stroke = sb, StrokeThickness = 1 });
            }
            int n = _runs.Sum(r => r.Count);
            Text(n + " estribos por rectangulo", 8, H - 18, Brushes.DimGray, 11);
        }

        private void Text(string s, double x, double y, Brush brush, double size)
        {
            var t = new TextBlock { Text = s, Foreground = brush, FontSize = size };
            SetLeft(t, x); SetTop(t, y);
            Children.Add(t);
        }
    }
}
