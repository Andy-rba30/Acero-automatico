using System;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace ColumnRebar
{
    /// <summary>
    /// Al arrancar Revit crea (o reutiliza) la pestana "ARBA" y anade el panel "Columnas" con
    /// el boton "Armar columna", que lanza ArmarColumnaCommand. El comando sigue disponible
    /// ademas en Complementos > Herramientas externas. El icono se dibuja en codigo.
    /// </summary>
    public class RibbonApp : IExternalApplication
    {
        public const string TabName = "ARBA";
        private const string PanelName = "Columnas";

        public Result OnStartup(UIControlledApplication app)
        {
            try
            {
                try { app.CreateRibbonTab(TabName); } catch (Exception) { }

                RibbonPanel panel = null;
                foreach (RibbonPanel p in app.GetRibbonPanels(TabName))
                    if (p.Name == PanelName) { panel = p; break; }
                if (panel == null) panel = app.CreateRibbonPanel(TabName, PanelName);

                string assembly = Assembly.GetExecutingAssembly().Location;
                var data = new PushButtonData("ArmarColumna", "Armar\ncolumna", assembly, typeof(ArmarColumnaCommand).FullName)
                {
                    ToolTip = "Genera el armado de columnas de seccion rectangular, L, T, U o cruz: longitudinales, estribos y grapas",
                    LongDescription = "Selecciona una o varias columnas estructurales y pulsa el boton. Se abre la ventana " +
                                      "para elegir los tipos de barra, la separacion de longitudinales, la distribucion de " +
                                      "estribos y los ganchos, con un esquema de la seccion y del alzado. Si no hay nada " +
                                      "seleccionado, el comando pide que elijas las columnas.",
                    LargeImage = Icon(32),
                    Image = Icon(16)
                };
                panel.AddItem(data);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("ARBA", "No se pudo crear el panel Columnas: " + ex.Message +
                                "\nEl comando sigue disponible en Complementos > Herramientas externas.");
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication app) => Result.Succeeded;

        /// <summary>Icono: seccion en L con sus dos estribos y las barras longitudinales.</summary>
        private static BitmapSource Icon(int size)
        {
            double s = size / 32.0;
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                var concrete = new SolidColorBrush(Color.FromRgb(0xD9, 0xD9, 0xD9));
                var edge = new Pen(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)), 1.2 * s);
                var stirrup1 = new Pen(new SolidColorBrush(Color.FromRgb(0x1F, 0x7A, 0x7A)), 1.6 * s) { LineJoin = PenLineJoin.Round };
                var stirrup2 = new Pen(new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x2E)), 1.6 * s) { LineJoin = PenLineJoin.Round };
                var bar = new SolidColorBrush(Color.FromRgb(0x8B, 0x2E, 0x2E));

                // hormigon en L
                var outline = new StreamGeometry();
                using (StreamGeometryContext g = outline.Open())
                {
                    g.BeginFigure(new Point(2 * s, 2 * s), true, true);
                    g.LineTo(new Point(30 * s, 2 * s), true, false);
                    g.LineTo(new Point(30 * s, 14 * s), true, false);
                    g.LineTo(new Point(14 * s, 14 * s), true, false);
                    g.LineTo(new Point(14 * s, 30 * s), true, false);
                    g.LineTo(new Point(2 * s, 30 * s), true, false);
                }
                dc.DrawGeometry(concrete, edge, outline);

                // estribos: uno en la rama horizontal y otro en la vertical
                dc.DrawRectangle(null, stirrup1, new System.Windows.Rect(5 * s, 5 * s, 22 * s, 6 * s));
                dc.DrawRectangle(null, stirrup2, new System.Windows.Rect(5 * s, 5 * s, 6 * s, 22 * s));

                // barras en las esquinas de los estribos
                double rr = 1.7 * s;
                foreach (Point p in new[]
                {
                    new Point(5 * s, 5 * s), new Point(27 * s, 5 * s), new Point(27 * s, 11 * s),
                    new Point(11 * s, 11 * s), new Point(11 * s, 27 * s), new Point(5 * s, 27 * s), new Point(5 * s, 11 * s)
                })
                    dc.DrawEllipse(bar, null, p, rr, rr);
            }
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }
    }
}
