using System;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace RetainingWallRebar
{
    /// <summary>
    /// Gestion compartida de la pestana "ARBA" y sus paneles (IA, Acero, Encofrado).
    /// Asegura el mismo orden sin importar que add-in cargue primero.
    /// </summary>
    public static class ArbaRibbon
    {
        public const string TabName = "ARBA";
        public const string PanelIaName = "IA";
        public const string PanelAceroName = "Acero";
        public const string PanelEncofradoName = "Encofrado";

        private static readonly string[] OrderedPanels = { PanelIaName, PanelAceroName, PanelEncofradoName };

        /// <summary>
        /// Crea la pestana "ARBA" si no existe y los paneles "IA", "Acero" y "Encofrado"
        /// siempre en este orden estricto. Cada panel nuevo inicia oculto (Visible = false).
        /// </summary>
        public static void Ensure(UIControlledApplication app)
        {
            try
            {
                app.CreateRibbonTab(TabName);
            }
            catch (Exception)
            {
                // Ya creada por otro add-in
            }

            var existing = app.GetRibbonPanels(TabName);
            foreach (string panelName in OrderedPanels)
            {
                bool exists = false;
                if (existing != null)
                {
                    foreach (RibbonPanel p in existing)
                    {
                        if (string.Equals(p.Name, panelName, StringComparison.OrdinalIgnoreCase))
                        {
                            exists = true;
                            break;
                        }
                    }
                }

                if (!exists)
                {
                    RibbonPanel panel = app.CreateRibbonPanel(TabName, panelName);
                    panel.Visible = false;
                }
            }
        }

        /// <summary>
        /// Obtiene el panel solicitado de la pestana ARBA. Si no existe, lo crea.
        /// </summary>
        public static RibbonPanel GetPanel(UIControlledApplication app, string panelName)
        {
            var existing = app.GetRibbonPanels(TabName);
            if (existing != null)
            {
                foreach (RibbonPanel p in existing)
                {
                    if (string.Equals(p.Name, panelName, StringComparison.OrdinalIgnoreCase))
                        return p;
                }
            }

            RibbonPanel created = app.CreateRibbonPanel(TabName, panelName);
            created.Visible = false;
            return created;
        }

        /// <summary>
        /// Busca un PulldownButton con el nombre dado en el panel. Si no existe lo crea con
        /// su icono correspondiente y le anade el PushButton. Al anadir, pone el panel visible.
        /// </summary>
        public static void AddToPulldown(UIControlledApplication app, string panelName, string pulldownName, PushButtonData data)
        {
            RibbonPanel panel = GetPanel(app, panelName);

            PulldownButton pulldown = null;
            var items = panel.GetItems();
            if (items != null)
            {
                foreach (RibbonItem item in items)
                {
                    if (item is PulldownButton pb && string.Equals(pb.Name, pulldownName, StringComparison.OrdinalIgnoreCase))
                    {
                        pulldown = pb;
                        break;
                    }
                }
            }

            if (pulldown == null)
            {
                var pbData = new PulldownButtonData(pulldownName, pulldownName);
                if (string.Equals(pulldownName, PanelAceroName, StringComparison.OrdinalIgnoreCase))
                {
                    pbData.ToolTip = "Herramientas de armado de acero";
                    pbData.LargeImage = IconAcero(32);
                    pbData.Image = IconAcero(16);
                }
                else if (string.Equals(pulldownName, PanelEncofradoName, StringComparison.OrdinalIgnoreCase))
                {
                    pbData.ToolTip = "Herramientas de metrado de encofrado";
                    pbData.LargeImage = IconEncofrado(32);
                    pbData.Image = IconEncofrado(16);
                }
                pulldown = panel.AddItem(pbData) as PulldownButton;
            }

            pulldown?.AddPushButton(data);
            panel.Visible = true;
        }

        /// <summary>Icono para el desplegable y botones de Acero (seccion en L con estribos y barras).</summary>
        public static BitmapSource IconAcero(int size)
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

                dc.DrawRectangle(null, stirrup1, new System.Windows.Rect(5 * s, 5 * s, 22 * s, 6 * s));
                dc.DrawRectangle(null, stirrup2, new System.Windows.Rect(5 * s, 5 * s, 6 * s, 22 * s));

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

        /// <summary>Icono para el desplegable y botones de Encofrado (seccion con tableros de madera).</summary>
        public static BitmapSource IconEncofrado(int size)
        {
            double s = size / 32.0;
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                var concrete = new SolidColorBrush(Color.FromRgb(0xD9, 0xD9, 0xD9));
                var edge = new Pen(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)), 1.2 * s);
                var board = new Pen(new SolidColorBrush(Color.FromRgb(0xC8, 0x7A, 0x1E)), 2.6 * s)
                {
                    StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat
                };

                var outline = new StreamGeometry();
                using (StreamGeometryContext g = outline.Open())
                {
                    g.BeginFigure(new Point(3 * s, 30 * s), true, true);
                    g.LineTo(new Point(29 * s, 30 * s), true, false);
                    g.LineTo(new Point(29 * s, 23 * s), true, false);
                    g.LineTo(new Point(19 * s, 23 * s), true, false);
                    g.LineTo(new Point(18 * s, 2 * s), true, false);
                    g.LineTo(new Point(13 * s, 2 * s), true, false);
                    g.LineTo(new Point(10 * s, 23 * s), true, false);
                    g.LineTo(new Point(3 * s, 23 * s), true, false);
                }
                dc.DrawGeometry(concrete, edge, outline);

                dc.DrawLine(board, new Point(11.6 * s, 3 * s), new Point(8.6 * s, 22.5 * s));
                dc.DrawLine(board, new Point(19.5 * s, 3 * s), new Point(20.5 * s, 22.5 * s));
                dc.DrawLine(board, new Point(1.6 * s, 23 * s), new Point(1.6 * s, 30 * s));
                dc.DrawLine(board, new Point(30.4 * s, 23 * s), new Point(30.4 * s, 30 * s));
            }

            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }
    }

    /// <summary>
    /// Entrada de la aplicacion de cinta para RetainingWallRebar en Revit.
    /// Anade el boton "Muro de contencion" al desplegable "Acero" del panel "Acero" en la pestana "ARBA".
    /// </summary>
    public class RibbonApp : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication app)
        {
            try
            {
                ArbaRibbon.Ensure(app);

                string assembly = Assembly.GetExecutingAssembly().Location;
                var data = new PushButtonData("ARBA_Acero_Muro", "Muro de contencion", assembly,
                                              typeof(ArmarMuroCommand).FullName)
                {
                    ToolTip = "Genera el armado de muros de contencion (tramos rectos y esquineros en L)",
                    LongDescription = "Selecciona uno o varios muros y pulsa el boton. Se abre la ventana para elegir el " +
                                      "armado y los tipos de barra, con un esquema del muro marcado. Si no hay nada " +
                                      "seleccionado, el comando pide que elijas los muros.",
                    LargeImage = Icon(32),
                    Image = Icon(16)
                };

                ArbaRibbon.AddToPulldown(app, ArbaRibbon.PanelAceroName, ArbaRibbon.PanelAceroName, data);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("ARBA", "No se pudo anadir el boton Muro de contencion a la cinta: " + ex.Message +
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
