using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ColumnRebar
{
    /// <summary>
    /// Ventana previa al armado: muestra que se ha detectado en cada columna seleccionada
    /// (forma de la seccion, numero de estribos, altura, o el motivo del rechazo) y deja
    /// elegir el armado: tipos de barra, separacion de longitudinales, prolongaciones,
    /// distribucion de estribos (global y por elemento), ganchos, grapas y recubrimiento,
    /// con un esquema de la seccion y del alzado que se redibuja con cada cambio.
    /// Los valores iniciales vienen de config.json y se pueden guardar como nuevos valores
    /// por defecto. Construida en codigo (sin XAML).
    /// </summary>
    public sealed class RebarOptionsWindow : Window
    {
        private readonly AppConfig _cfg;
        private readonly IList<string> _barTypes;
        private readonly IDictionary<string, double> _diametersMm;
        private readonly Dictionary<string, string> _typeByDisplay = new Dictionary<string, string>();
        private readonly IList<string> _hookTypes;
        private readonly IList<HostAnalysis> _items;

        /// <summary>Configuracion final si el usuario pulso "Armar"; null si cancelo.</summary>
        public AppConfig Result { get; private set; }

        private ComboBox _longType, _stType, _stHook, _stOrient, _tieType, _tieHook, _tieOrient, _tieDir;
        private TextBox _longSpacing, _longBottom, _longTop, _longLeg;
        private TextBox _stDist, _stBottomOff, _stTopOff, _cover, _partition;
        private CheckBox _stSym, _tieOn;
        private TextBlock _message, _partitionPreview, _previewCaption;
        private Button _buildButton;
        private SectionPreview _preview;
        private ElevationPreview _elevation;

        private readonly Dictionary<HostAnalysis, (System.Windows.Documents.Run kind, System.Windows.Documents.Run detail)> _itemRuns
            = new Dictionary<HostAnalysis, (System.Windows.Documents.Run, System.Windows.Documents.Run)>();
        private readonly Dictionary<HostAnalysis, Border> _itemRows = new Dictionary<HostAnalysis, Border>();
        private HostAnalysis _selected;
        private bool _building = true;
        private bool _strictTypes;

        private const string NoHook = "(sin gancho)";
        private static readonly Thickness Pad = new Thickness(4, 2, 4, 2);
        private static readonly Brush SelectedBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0xE8, 0xF6));

        public RebarOptionsWindow(AppConfig cfg, IList<string> barTypes, IDictionary<string, double> diametersMm,
                                  IList<string> hookTypes, IList<HostAnalysis> items)
        {
            _cfg = cfg;
            _cfg.Normalize();
            _diametersMm = diametersMm;
            _barTypes = barTypes.OrderBy(n => diametersMm.TryGetValue(n, out double mm) ? mm : 0)
                                .ThenBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (string n in _barTypes) _typeByDisplay[TypeDisplay(n)] = n;
            _hookTypes = hookTypes;
            _items = items;

            Title = "Armar columnas";
            Width = 1180;
            Height = 820;
            MinWidth = 980;
            MinHeight = 620;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            FontSize = 12;

            Content = BuildRoot();
            _selected = _items.FirstOrDefault(i => i.CanBuild);
            if (_selected != null) SelectItem(_selected);
            _building = false;
            Refresh();
        }

        // ------------------------------------------------------------------
        // Construccion de la interfaz
        // ------------------------------------------------------------------
        private UIElement BuildRoot()
        {
            var root = new Grid { Margin = new Thickness(10) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            UIElement elements = BuildElements();
            Grid.SetRow(elements, 0);
            root.Children.Add(elements);

            var body = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });

            var left = new StackPanel();
            left.Children.Add(BuildLongitudinal());
            left.Children.Add(BuildStirrups());
            left.Children.Add(BuildTies());
            left.Children.Add(BuildGeneral());
            var scroll = new ScrollViewer
            {
                Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(scroll, 0);
            body.Children.Add(scroll);

            UIElement previews = BuildPreviews();
            Grid.SetColumn(previews, 1);
            body.Children.Add(previews);

            Grid.SetRow(body, 1);
            root.Children.Add(body);

            UIElement buttons = BuildButtons();
            Grid.SetRow(buttons, 2);
            root.Children.Add(buttons);
            return root;
        }

        private UIElement BuildElements()
        {
            int ok = _items.Count(i => i.CanBuild);
            var group = new GroupBox
            {
                Header = "Columnas seleccionadas: " + _items.Count + " (" + ok + " armables). Haz clic en una para verla en el esquema. " +
                         "Los campos de la derecha son propios de cada columna (vacio = valor general).",
                Padding = new Thickness(4)
            };
            var panel = new StackPanel();
            foreach (HostAnalysis item in _items)
            {
                var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var text = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                text.Inlines.Add(new System.Windows.Documents.Run(item.Tag) { FontWeight = FontWeights.Bold });
                var kindRun = new System.Windows.Documents.Run(item.Kind + ": ")
                {
                    FontWeight = FontWeights.SemiBold,
                    Foreground = item.CanBuild ? Brushes.DarkGreen : Brushes.Firebrick
                };
                var detailRun = new System.Windows.Documents.Run(item.Detail(_cfg));
                text.Inlines.Add(kindRun);
                text.Inlines.Add(detailRun);
                _itemRuns[item] = (kindRun, detailRun);
                Grid.SetColumn(text, 0);
                row.Children.Add(text);

                if (item.CanBuild)
                {
                    var side = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                    side.Children.Add(new TextBlock { Text = "Estribos:", Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
                    var dist = new TextBox { Width = 150, Text = item.DistributionOverride, ToolTip = "Distribucion de estribos de esta columna, como \"1@50, 5@100, R@250\". Vacio = la general." };
                    HostAnalysis captured = item;
                    dist.TextChanged += (s, e) => { captured.DistributionOverride = dist.Text; Refresh(); };
                    side.Children.Add(dist);
                    side.Children.Add(new TextBlock { Text = "Sep. long. (mm):", Margin = new Thickness(8, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
                    var sp = new TextBox { Width = 48, Text = item.SpacingOverride > 0 ? Num(item.SpacingOverride) : "", ToolTip = "Separacion maxima entre longitudinales de esta columna (mm). Vacio = la general." };
                    sp.TextChanged += (s, e) =>
                    {
                        captured.SpacingOverride = StirrupLayout.TryNumber(sp.Text, out double v) && v > 0 ? v : 0;
                        Refresh();
                    };
                    side.Children.Add(sp);
                    Grid.SetColumn(side, 1);
                    row.Children.Add(side);
                }

                var border = new Border { Child = row, Padding = new Thickness(4, 2, 4, 2), CornerRadius = new CornerRadius(3) };
                if (item.CanBuild)
                {
                    border.Cursor = Cursors.Hand;
                    HostAnalysis captured = item;
                    border.MouseLeftButtonDown += (s, e) => { SelectItem(captured); Refresh(); };
                }
                _itemRows[item] = border;
                panel.Children.Add(border);
            }
            group.Content = new ScrollViewer
            {
                Content = panel, MaxHeight = 150,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            return group;
        }

        private void SelectItem(HostAnalysis item)
        {
            _selected = item;
            foreach (var kv in _itemRows)
                kv.Value.Background = kv.Key == item ? SelectedBrush : Brushes.Transparent;
        }

        private UIElement BuildLongitudinal()
        {
            var group = new GroupBox { Header = "Barras longitudinales", Padding = new Thickness(4) };
            var grid = FormGrid();
            int r = 0;
            _longType = TypeCombo(_cfg.Longitudinal.BarTypeName);
            AddRow(grid, r++, "Tipo de barra:", _longType, "Tipo de barra (RebarBarType) de las longitudinales.");
            _longSpacing = NumBox(_cfg.Longitudinal.MaxSpacingMm);
            AddRow(grid, r++, "Separacion maxima (mm):", _longSpacing,
                   "Separacion maxima eje a eje entre longitudinales a lo largo de cada lado de cada estribo. Siempre hay barra en las " +
                   "esquinas de cada estribo y donde un estribo cruza a otro; entre ellas se anaden las intermedias necesarias.");
            _longBottom = NumBox(_cfg.Longitudinal.BottomExtensionMm);
            AddRow(grid, r++, "Prolongacion inferior (mm):", _longBottom,
                   "Cuanto sobresalen las barras por debajo de la base de la columna (anclaje en la cimentacion o en el piso inferior). 0 = empiezan en la base.");
            _longTop = NumBox(_cfg.Longitudinal.TopExtensionMm);
            AddRow(grid, r++, "Prolongacion superior (mm):", _longTop,
                   "Cuanto sobresalen por encima de la coronacion (empalme con el piso siguiente). 0 = terminan en la coronacion.");
            _longLeg = NumBox(_cfg.Longitudinal.BottomLegMm);
            AddRow(grid, r++, "Patilla inferior (mm):", _longLeg,
                   "Patilla horizontal a 90 grados en el extremo inferior, hacia el centro de la seccion. Solo si hay prolongacion inferior. 0 = sin patilla.");
            group.Content = grid;
            return group;
        }

        private UIElement BuildStirrups()
        {
            var group = new GroupBox { Header = "Estribos (uno cerrado por cada rectangulo de la seccion)", Padding = new Thickness(4) };
            var grid = FormGrid();
            int r = 0;
            _stType = TypeCombo(_cfg.Stirrups.BarTypeName);
            AddRow(grid, r++, "Tipo de barra:", _stType, "Tipo de barra de los estribos.");
            _stHook = HookCombo(_cfg.Stirrups.HookTypeName);
            AddRow(grid, r++, "Gancho:", _stHook, "Tipo de gancho (RebarHookType) en los dos extremos del estribo, normalmente 135 grados. Sin gancho = estribo cerrado sin ganchos.");
            _stOrient = OrientCombo(_cfg.HookLeft);
            AddRow(grid, r++, "Giro del gancho:", _stOrient,
                   "Lado hacia el que giran los ganchos. Si con la orientacion elegida quedan fuera del hormigon, el plugin la invierte solo y lo avisa.");
            _stDist = new TextBox { Text = _cfg.Stirrups.Distribution, Margin = Pad };
            AddRow(grid, r++, "Distribucion:", _stDist,
                   "Como en los planos: \"1@50, 5@100, R@250\" = el primero a 50 mm de la base, cinco mas cada 100 mm y el resto cada 250 mm " +
                   "como maximo (repartidos por igual). Valores menores de 5 se leen en metros (\"1@.05\"). Cada columna puede tener la suya en la lista de arriba.");
            _stSym = new CheckBox { Content = "Repetir los grupos desde la coronacion (confinamiento arriba y abajo)", IsChecked = _cfg.Stirrups.Symmetric, Margin = Pad };
            AddRow(grid, r++, "", _stSym, "Con la casilla marcada, los grupos fijos (1@50, 5@100...) se colocan tambien desde arriba en espejo y el resto va en medio.");
            _stBottomOff = NumBox(_cfg.Stirrups.BottomOffsetMm);
            AddRow(grid, r++, "Desfase en la base (mm):", _stBottomOff, "La distribucion empieza a contar desde la base mas este desfase.");
            _stTopOff = NumBox(_cfg.Stirrups.TopOffsetMm);
            AddRow(grid, r++, "Desfase en coronacion (mm):", _stTopOff, "La distribucion termina en la coronacion menos este desfase (por ejemplo el canto de la losa si el elemento la incluye).");
            group.Content = grid;
            return group;
        }

        private UIElement BuildTies()
        {
            var group = new GroupBox { Header = "Grapas entre barras intermedias enfrentadas", Padding = new Thickness(4) };
            var grid = FormGrid();
            int r = 0;
            _tieOn = new CheckBox { Content = "Colocar grapas en cada cota de estribo", IsChecked = _cfg.Crossties.Enabled, Margin = Pad };
            AddRow(grid, r++, "", _tieOn,
                   "Una grapa por cada par de barras intermedias enfrentadas de un mismo estribo (salvo donde ya pasa el lado de otro estribo).");
            _tieType = TypeCombo(_cfg.Crossties.BarTypeName);
            AddRow(grid, r++, "Tipo de barra:", _tieType, "Tipo de barra de las grapas.");
            _tieHook = HookCombo(_cfg.Crossties.HookTypeName);
            AddRow(grid, r++, "Gancho:", _tieHook, "Tipo de gancho en los dos extremos de la grapa.");
            _tieOrient = OrientCombo(_cfg.TieHookLeft);
            AddRow(grid, r++, "Giro del gancho:", _tieOrient, "Lado hacia el que giran los ganchos de la grapa (se invierte solo si quedan fuera).");
            _tieDir = new ComboBox { Margin = Pad };
            _tieDir.Items.Add("En las dos direcciones");
            _tieDir.Items.Add("Solo paralelas al lado largo (u)");
            _tieDir.Items.Add("Solo paralelas al lado corto (v)");
            _tieDir.SelectedIndex = _cfg.TiesU && _cfg.TiesV ? 0 : _cfg.TiesU ? 1 : 2;
            AddRow(grid, r++, "Direccion:", _tieDir, "u es el eje del borde mas largo de la seccion; v el perpendicular.");
            group.Content = grid;
            return group;
        }

        private UIElement BuildGeneral()
        {
            var group = new GroupBox { Header = "Recubrimiento y particion", Padding = new Thickness(4) };
            var grid = FormGrid();
            int r = 0;
            _cover = NumBox(_cfg.CoverMm);
            AddRow(grid, r++, "Recubrimiento al estribo (mm):", _cover, "Distancia de cada cara de la columna al borde exterior del estribo.");
            _partition = new TextBox { Text = _cfg.PartitionTemplate, Margin = Pad };
            AddRow(grid, r++, "Particion:", _partition, "Plantilla del parametro Particion de cada barra. Comodines: " + PartitionName.Help);
            _partitionPreview = new TextBlock { Foreground = Brushes.DimGray, Margin = Pad, TextWrapping = TextWrapping.Wrap };
            AddRow(grid, r++, "", _partitionPreview, null);
            group.Content = grid;
            return group;
        }

        private UIElement BuildPreviews()
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.35, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var secGroup = new GroupBox { Header = "Seccion (rueda: zoom, arrastrar: mover, doble clic: encajar)", Padding = new Thickness(4) };
            var secPanel = new DockPanel();
            _previewCaption = new TextBlock { Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
            DockPanel.SetDock(_previewCaption, Dock.Top);
            secPanel.Children.Add(_previewCaption);
            var legend = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            LegendItem(legend, SectionPreview.RequiredBrush, "barra de esquina o cruce");
            LegendItem(legend, SectionPreview.IntermediateBrush, "barra intermedia");
            LegendItem(legend, SectionPreview.StirrupBrush(0), "estribo 1");
            LegendItem(legend, SectionPreview.StirrupBrush(1), "estribo 2...");
            LegendItem(legend, SectionPreview.TieBrush, "grapa");
            DockPanel.SetDock(legend, Dock.Bottom);
            secPanel.Children.Add(legend);
            _preview = new SectionPreview { MinHeight = 200 };
            secPanel.Children.Add(new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), Child = _preview });
            secGroup.Content = secPanel;
            Grid.SetRow(secGroup, 0);
            grid.Children.Add(secGroup);

            var elvGroup = new GroupBox { Header = "Alzado: distribucion de estribos", Padding = new Thickness(4), Margin = new Thickness(0, 6, 0, 0) };
            _elevation = new ElevationPreview { MinHeight = 150 };
            elvGroup.Content = new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), Child = _elevation };
            Grid.SetRow(elvGroup, 1);
            grid.Children.Add(elvGroup);
            return grid;
        }

        private static void LegendItem(Panel panel, Brush brush, string text)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
            sp.Children.Add(new System.Windows.Shapes.Rectangle { Width = 12, Height = 12, Fill = brush, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(sp);
        }

        private UIElement BuildButtons()
        {
            var panel = new DockPanel();
            _message = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            DockPanel.SetDock(buttons, Dock.Right);

            var save = new Button { Content = "Guardar como valores por defecto", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(4, 0, 4, 0) };
            save.ToolTip = "Guarda lo elegido en config.json (" + AppConfig.ConfigPath() + ") para las proximas veces.";
            save.Click += (s, e) =>
            {
                AppConfig c = ReadConfig(out string err);
                if (err != null) { _message.Text = err; return; }
                try { c.Save(); _message.Foreground = Brushes.DarkGreen; _message.Text = "Guardado en " + AppConfig.ConfigPath(); }
                catch (Exception ex) { _message.Foreground = Brushes.Firebrick; _message.Text = "No se pudo guardar: " + ex.Message; }
            };
            buttons.Children.Add(save);

            _buildButton = new Button { Content = "Armar", Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(4, 0, 4, 0), FontWeight = FontWeights.SemiBold, IsDefault = true };
            _buildButton.Click += (s, e) => OnBuild();
            buttons.Children.Add(_buildButton);

            var cancel = new Button { Content = "Cancelar", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(4, 0, 0, 0), IsCancel = true };
            buttons.Children.Add(cancel);

            panel.Children.Add(buttons);
            panel.Children.Add(_message);
            return panel;
        }

        // ------------------------------------------------------------------
        // Controles auxiliares
        // ------------------------------------------------------------------
        private static Grid FormGrid()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            return grid;
        }

        private void AddRow(Grid grid, int row, string label, FrameworkElement control, string tip)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var lb = new TextBlock { Text = label, Margin = Pad, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(lb, row); Grid.SetColumn(lb, 0);
            grid.Children.Add(lb);
            if (tip != null) { control.ToolTip = tip; lb.ToolTip = tip; }
            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(control, row); Grid.SetColumn(control, 1);
            grid.Children.Add(control);
            Hook(control);
        }

        private void Hook(FrameworkElement c)
        {
            if (c is TextBox tb) tb.TextChanged += (s, e) => Refresh();
            else if (c is ComboBox cb) cb.SelectionChanged += (s, e) => Refresh();
            else if (c is CheckBox ck) { ck.Checked += (s, e) => Refresh(); ck.Unchecked += (s, e) => Refresh(); }
        }

        private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        private static TextBox NumBox(double v) => new TextBox { Text = Num(v), Width = 80, HorizontalAlignment = HorizontalAlignment.Left, Margin = Pad };

        private string TypeDisplay(string name) =>
            _diametersMm.TryGetValue(name, out double mm) ? name + " (" + Num(mm) + " mm)" : name;

        private ComboBox TypeCombo(string current)
        {
            var cb = new ComboBox { Margin = Pad };
            foreach (string n in _barTypes) cb.Items.Add(TypeDisplay(n));
            string match = RebarGenerator.MatchName(_barTypes, current);
            cb.SelectedIndex = match == null ? -1 : _barTypes.IndexOf(match);
            return cb;
        }

        private string TypeOf(ComboBox cb) =>
            cb.SelectedItem is string d && _typeByDisplay.TryGetValue(d, out string n) ? n : "";

        private ComboBox HookCombo(string current)
        {
            var cb = new ComboBox { Margin = Pad };
            cb.Items.Add(NoHook);
            foreach (string n in _hookTypes) cb.Items.Add(n);
            string match = RebarGenerator.MatchName(_hookTypes, current);
            cb.SelectedIndex = match == null ? 0 : _hookTypes.IndexOf(match) + 1;
            return cb;
        }

        private static string HookOf(ComboBox cb) => cb.SelectedIndex <= 0 ? "" : (string)cb.SelectedItem;

        private static ComboBox OrientCombo(bool left)
        {
            var cb = new ComboBox { Margin = Pad, Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
            cb.Items.Add("Izquierda");
            cb.Items.Add("Derecha");
            cb.SelectedIndex = left ? 0 : 1;
            return cb;
        }

        private double DiameterFt(string typeName)
        {
            string match = RebarGenerator.MatchName(_barTypes, typeName);
            return match != null && _diametersMm.TryGetValue(match, out double mm) ? ColumnSection.Mm(mm) : 0;
        }

        // ------------------------------------------------------------------
        // Lectura de la configuracion desde los controles
        // ------------------------------------------------------------------
        private AppConfig ReadConfig(out string error)
        {
            var errors = new List<string>();
            AppConfig c = _cfg.Clone();

            c.Longitudinal.BarTypeName = TypeOf(_longType);
            c.Longitudinal.MaxSpacingMm = ReadNum(_longSpacing, "separacion maxima", 1, errors);
            c.Longitudinal.BottomExtensionMm = ReadNum(_longBottom, "prolongacion inferior", 0, errors);
            c.Longitudinal.TopExtensionMm = ReadNum(_longTop, "prolongacion superior", 0, errors);
            c.Longitudinal.BottomLegMm = ReadNum(_longLeg, "patilla inferior", 0, errors);

            c.Stirrups.BarTypeName = TypeOf(_stType);
            c.Stirrups.HookTypeName = HookOf(_stHook);
            c.Stirrups.HookOrientation = _stOrient.SelectedIndex == 1 ? "right" : "left";
            c.Stirrups.Distribution = _stDist.Text.Trim();
            if (StirrupLayout.Parse(c.Stirrups.Distribution, out string derr) == null) errors.Add("distribucion de estribos: " + derr);
            c.Stirrups.Symmetric = _stSym.IsChecked == true;
            c.Stirrups.BottomOffsetMm = ReadNum(_stBottomOff, "desfase en la base", 0, errors);
            c.Stirrups.TopOffsetMm = ReadNum(_stTopOff, "desfase en coronacion", 0, errors);

            c.Crossties.Enabled = _tieOn.IsChecked == true;
            c.Crossties.BarTypeName = TypeOf(_tieType);
            c.Crossties.HookTypeName = HookOf(_tieHook);
            c.Crossties.HookOrientation = _tieOrient.SelectedIndex == 1 ? "right" : "left";
            c.Crossties.Directions = _tieDir.SelectedIndex == 1 ? "u" : _tieDir.SelectedIndex == 2 ? "v" : "both";

            c.CoverMm = ReadNum(_cover, "recubrimiento", 0, errors);
            c.PartitionTemplate = _partition.Text.Trim();
            c.Normalize();

            error = errors.Count == 0 ? null : string.Join(" | ", errors);
            return c;
        }

        private static double ReadNum(TextBox tb, string label, double min, List<string> errors)
        {
            if (!StirrupLayout.TryNumber(tb.Text, out double v) || v < min)
            {
                errors.Add(label + ": numero no valido" + (min > 0 ? " (minimo " + Num(min) + ")" : ""));
                tb.BorderBrush = Brushes.Firebrick;
                return min;
            }
            tb.ClearValue(Control.BorderBrushProperty);
            return v;
        }

        /// <summary>Tipos de barra que faltan (obligatorios segun lo activado).</summary>
        private List<string> MissingTypes(AppConfig c)
        {
            var missing = new List<string>();
            if (string.IsNullOrEmpty(c.Longitudinal.BarTypeName)) missing.Add("longitudinales");
            if (string.IsNullOrEmpty(c.Stirrups.BarTypeName)) missing.Add("estribos");
            if (c.Crossties.Enabled && string.IsNullOrEmpty(c.Crossties.BarTypeName)) missing.Add("grapas");
            return missing;
        }

        private void MarkTypes(AppConfig c)
        {
            foreach ((ComboBox cb, bool required) in new[]
            {
                (_longType, true), (_stType, true), (_tieType, c.Crossties.Enabled)
            })
            {
                if (_strictTypes && required && cb.SelectedIndex < 0) { cb.BorderBrush = Brushes.Firebrick; cb.BorderThickness = new Thickness(2); }
                else { cb.ClearValue(Control.BorderBrushProperty); cb.ClearValue(Control.BorderThicknessProperty); }
            }
        }

        // ------------------------------------------------------------------
        // Actualizacion
        // ------------------------------------------------------------------
        private void Refresh()
        {
            if (_building) return;
            AppConfig scratch = ReadConfig(out string error);
            MarkTypes(scratch);
            bool tiesEnabled = scratch.Crossties.Enabled;
            foreach (FrameworkElement fe in new FrameworkElement[] { _tieType, _tieHook, _tieOrient, _tieDir }) fe.IsEnabled = tiesEnabled;

            double db = DiameterFt(scratch.Longitudinal.BarTypeName);
            double ds = DiameterFt(scratch.Stirrups.BarTypeName);
            double dt = tiesEnabled ? DiameterFt(scratch.Crossties.BarTypeName) : 0;
            // sin tipo elegido, se dibuja con un diametro orientativo para poder ver el esquema
            double dbDraw = db > 0 ? db : ColumnSection.Mm(16), dsDraw = ds > 0 ? ds : ColumnSection.Mm(8), dtDraw = dt > 0 ? dt : dsDraw;

            // estado de cada columna con esta configuracion
            int ok = 0;
            foreach (HostAnalysis item in _items)
            {
                bool good = ItemStatus(item, scratch, dbDraw, dsDraw, dtDraw, out string text, out _, out _);
                if (good) ok++;
                if (_itemRuns.TryGetValue(item, out var runs))
                {
                    runs.kind.Foreground = good ? Brushes.DarkGreen : Brushes.Firebrick;
                    runs.detail.Text = text;
                }
            }
            _buildButton.Content = "Armar " + ok + " elemento(s)";
            _buildButton.IsEnabled = ok > 0 && error == null;

            // esquema del elemento seleccionado
            if (_selected != null && _selected.CanBuild)
            {
                ItemStatus(_selected, scratch, dbDraw, dsDraw, dtDraw, out string text, out ColumnPlan plan, out List<StirrupRun> runs);
                _previewCaption.Text = _selected.Tag + _selected.Section.Describe() +
                                       (db <= 0 || ds <= 0 ? "  (sin tipo de barra elegido: diametros orientativos)" : "");
                if (plan != null) _preview.Show(_selected.Section, plan); else _preview.Clear(text);
                if (runs != null) _elevation.Show(_selected.Section, plan, runs, scratch); else _elevation.Clear(text);
                _partitionPreview.Text = "Ejemplo: " + _selected.Partition(scratch, "estribo", "1");
            }
            else
            {
                _previewCaption.Text = "";
                _preview.Clear("Sin elemento armable");
                _elevation.Clear("");
                _partitionPreview.Text = "";
            }

            if (error != null) { _message.Foreground = Brushes.Firebrick; _message.Text = error; }
            else if (_message.Foreground == Brushes.Firebrick) _message.Text = "";
        }

        /// <summary>Estado de una columna con la configuracion dada: true si se puede armar, y el texto para su fila.</summary>
        private static bool ItemStatus(HostAnalysis item, AppConfig cfg, double db, double ds, double dt,
                                       out string text, out ColumnPlan plan, out List<StirrupRun> runs)
        {
            plan = null; runs = null;
            if (!item.CanBuild) { text = item.Error; return false; }
            try
            {
                plan = RebarGenerator.PlanFor(item, cfg, db, ds, dt);
                if (plan.Error != null) { text = item.Section.Describe() + " -> SIN ARMAR: " + plan.Error; return false; }
                runs = RebarGenerator.RunsFor(item, cfg, out string warn);
                int n = runs.Sum(r => r.Count);
                text = item.Section.Describe() + "; " + plan.Describe() + "; " + n + " estribos por rectangulo" +
                       (warn != null ? " (" + warn + ")" : "");
                return n > 0;
            }
            catch (Exception ex)
            {
                text = item.Section.Describe() + " -> SIN ARMAR: " + ex.Message;
                return false;
            }
        }

        private void OnBuild()
        {
            AppConfig c = ReadConfig(out string error);
            if (error != null) { _message.Foreground = Brushes.Firebrick; _message.Text = error; return; }
            List<string> missing = MissingTypes(c);
            if (missing.Count > 0)
            {
                _strictTypes = true;
                MarkTypes(c);
                _message.Foreground = Brushes.Firebrick;
                _message.Text = "Elige el tipo de barra de: " + string.Join(", ", missing) + ".";
                return;
            }
            Result = c;
            DialogResult = true;
            Close();
        }
    }
}
