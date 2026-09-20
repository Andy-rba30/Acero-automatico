using System;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace RetainingWallRebar
{
    /// <summary>
    /// Al arrancar Revit crea la pestana "ARBA" con el boton "Armar muro de contencion",
    /// que lanza ArmarMuroCommand. El comando sigue disponible ademas en Complementos,
    /// Herramientas externas (entrada de tipo Command del .addin). El icono se dibuja en
    /// codigo para no depender de archivos de imagen.
    /// </summary>
    public class RibbonApp : IExternalApplication
    {
        public const string TabName = "ARBA";
        private const string PanelName = "Muros de contencion";

        public Result OnStartup(UIControlledApplication app)
        {
            try
            {
                // la pestana puede existir ya si otro add-in la creo antes
                try { app.CreateRibbonTab(TabName); } catch (Exception) { }

                RibbonPanel panel = null;
                foreach (RibbonPanel p in app.GetRibbonPanels(TabName))
                    if (p.Name == PanelName) { panel = p; break; }
                if (panel == null) panel = app.CreateRibbonPanel(TabName, PanelName);

                string assembly = Assembly.GetExecutingAssembly().Location;
                var data = new PushButtonData("ArmarMuroContencion", "Armar muro\nde contencion", assembly,
                                              typeof(ArmarMuroCommand).FullName)
                {
                    ToolTip = "Genera el armado de muros de contencion (tramos rectos y esquineros en L)",
                    LongDescription = "Selecciona uno o varios muros y pulsa el boton. Se abre la ventana para elegir el " +
                                      "armado y los tipos de barra, con un esquema del muro marcado. Si no hay nada " +
                                      "seleccionado, el comando pide que elijas los muros.",
                    LargeImage = Icon(32),
                    Image = Icon(16)
                };
                panel.AddItem(data);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("ARBA", "No se pudo crear la pestana ARBA: " + ex.Message +
                                "\nEl comando sigue disponible en Complementos > Herramientas externas.");
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication app) => Result.Succeeded;

        /// <summary>
        /// Icono: seccion de un muro de contencion (zapata + pantalla en talud) con una
        /// vertical con patilla y la parrilla inferior, dibujado a la escala pedida.
        /// </summary>
        private static BitmapSource Icon(int size)
        {
            double s = size / 32.0;
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                var concrete = new SolidColorBrush(Color.FromRgb(0xD9, 0xD9, 0xD9));
                var edge = new Pen(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)), 1.2 * s);
                var bar = new Pen(new SolidColorBrush(Color.FromRgb(0x8B, 0x2E, 0x2E)), 2.2 * s)
                {
                    StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round
                };
                var mesh = new Pen(new SolidColorBrush(Color.FromRgb(0x1F, 0x7A, 0x7A)), 2.0 * s)
                {
                    StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round
                };

                // hormigon: zapata y pantalla con talud en el trasdos (izquierda)
                var outline = new StreamGeometry();
                using (StreamGeometryContext g = outline.Open())
                {
                    g.BeginFigure(new Point(2 * s, 30 * s), true, true);
                    g.LineTo(new Point(30 * s, 30 * s), true, false);
                    g.LineTo(new Point(30 * s, 23 * s), true, false);
                    g.LineTo(new Point(19 * s, 23 * s), true, false);
                    g.LineTo(new Point(18 * s, 2 * s), true, false);
                    g.LineTo(new Point(13 * s, 2 * s), true, false);
                    g.LineTo(new Point(10 * s, 23 * s), true, false);
                    g.LineTo(new Point(2 * s, 23 * s), true, false);
                }
                dc.DrawGeometry(concrete, edge, outline);

                // parrilla inferior de la zapata
                dc.DrawLine(mesh, new Point(5 * s, 27.5 * s), new Point(27 * s, 27.5 * s));

                // vertical del trasdos con patilla que cruza bajo la pantalla
                var vertical = new StreamGeometry();
                using (StreamGeometryContext g = vertical.Open())
                {
                    g.BeginFigure(new Point(15.2 * s, 4.5 * s), false, false);
                    g.LineTo(new Point(12.6 * s, 25.5 * s), true, true);
                    g.LineTo(new Point(24 * s, 25.5 * s), true, true);
                }
                dc.DrawGeometry(null, bar, vertical);
            }

            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }
    }
}
