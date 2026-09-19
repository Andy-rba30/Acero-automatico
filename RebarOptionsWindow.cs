using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RetainingWallRebar
{
    /// <summary>
    /// Ventana previa al armado: muestra que se ha detectado en cada elemento seleccionado
    /// (tramo recto, esquinero en L o rechazado con el motivo) y deja elegir a mano el
    /// armado (familias activas, tipo de barra, separaciones, patillas, recubrimientos y
    /// reglas de esquina). Los valores iniciales vienen de config.json y se pueden guardar
    /// como nuevos valores por defecto.
    /// Construida en codigo (sin XAML) para no depender del compilador de XAML.
    /// </summary>
    public sealed class RebarOptionsWindow : Window
    {
        private readonly AppConfig _cfg;
        private readonly IList<string> _barTypes;
        private readonly IList<HostAnalysis> _items;

        /// <summary>Configuracion final si el usuario pulso "Armar"; null si cancelo.</summary>
        public AppConfig Result { get; private set; }

        private sealed class FamilyRow
        {
            public BarFamilyCfg Cfg;
            public string Name;
            public CheckBox Enabled;
            public ComboBox Type;
            public TextBox Spacing, Leg, Cut;
        }

        private readonly List<FamilyRow> _rows = new List<FamilyRow>();
        private TextBox _covStem, _covStemTop, _covFootTop, _covFootBot, _covFootSide, _covEnd;
        private TextBox _band, _lapDia, _lapMin;
        private ComboBox _through, _mesh;

        private static readonly Thickness Pad = new Thickness(4, 2, 4, 2);

        public RebarOptionsWindow(AppConfig cfg, IList<string> barTypes, IList<HostAnalysis> items)
        {
            _cfg = cfg;
            _barTypes = barTypes;
            _items = items;

            Title = "Armar muros de contencion";
            Width = 1000;
            Height = 760;
            MinWidth = 820;
            MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            FontSize = 12;

            Content = BuildRoot();
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

            var body = new StackPanel();
            body.Children.Add(BuildFamilies());

            var two = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            two.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            two.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
            UIElement covers = BuildCovers();
            UIElement options = BuildOptions();
            Grid.SetColumn(covers, 0);
            Grid.SetColumn(options, 1);
            two.Children.Add(covers);
            two.Children.Add(options);
            body.Children.Add(two);

            var scroll = new ScrollViewer
            {
                Content = body,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 6, 0, 6)
            };
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);

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
                Header = "Elementos seleccionados: " + _items.Count + " (" + ok + " armables)",
                Padding = new Thickness(4)
            };
            var panel = new StackPanel();
            foreach (HostAnalysis item in _items)
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var text = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                text.Inlines.Add(new System.Windows.Documents.Run(item.Tag) { FontWeight = FontWeights.Bold });
                text.Inlines.Add(new System.Windows.Documents.Run(item.Kind + ": ")
                {
                    FontWeight = FontWeights.SemiBold,
                    Foreground = item.CanBuild ? Brushes.DarkGreen : Brushes.Firebrick
                });
                text.Inlines.Add(new System.Windows.Documents.Run(item.Detail(_cfg)));
                Grid.SetColumn(text, 0);
                row.Children.Add(text);

                if (item.Corner != null)
                {
                    var side = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                    side.Children.Add(new TextBlock { Text = "Ala pasante:", Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
                    var combo = new ComboBox { Width = 190 };
                    combo.Items.Add("Segun configuracion");
                    combo.Items.Add("Ala 1 (" + Mm(item.Corner.Wings[0].LenW) + " mm)");
                    combo.Items.Add("Ala 2 (" + Mm(item.Corner.Wings[1].LenW) + " mm)");
                    combo.SelectedIndex = item.Corner.ThroughChoice + 1;
                    CornerWall cw = item.Corner;
                    combo.SelectionChanged += (s, e) => cw.ThroughChoice = combo.SelectedIndex - 1;
                    side.Children.Add(combo);
                    Grid.SetColumn(side, 1);
                    row.Children.Add(side);
                }
                panel.Children.Add(row);
            }
            group.Content = new ScrollViewer
            {
                Content = panel,
                MaxHeight = 170,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            return group;
        }

        private UIElement BuildFamilies()
        {
            var group = new GroupBox { Header = "Familias de barras", Padding = new Thickness(4) };
            var grid = new Grid();
            string[] headers = { "Familia", "Activa", "Tipo de barra", "Separacion (mm)", "Patilla / pata (mm)", "Baston (mm)" };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < headers.Length; c++)
            {
                var h = new TextBlock { Text = headers[c], FontWeight = FontWeights.Bold, Margin = Pad };
                Grid.SetRow(h, 0); Grid.SetColumn(h, c);
                grid.Children.Add(h);
            }

            AddFamily(grid, "Vertical trasdos (alzado)", _cfg.StemVerticalBack, leg: true, cut: true, legHint: "patilla en zapata");
            AddFamily(grid, "Vertical intrados (alzado)", _cfg.StemVerticalFront, leg: true, cut: true, legHint: "patilla en zapata");
            AddFamily(grid, "Horizontal trasdos (alzado)", _cfg.StemHorizontalBack, leg: false, cut: false, legHint: null);
            AddFamily(grid, "Horizontal intrados (alzado)", _cfg.StemHorizontalFront, leg: false, cut: false, legHint: null);
            AddFamily(grid, "Transversal inferior (zapata)", _cfg.FootingTransverseBottom, leg: true, cut: false, legHint: "pata vertical en extremos");
            AddFamily(grid, "Transversal superior (zapata)", _cfg.FootingTransverseTop, leg: true, cut: false, legHint: "pata vertical en extremos");
            AddFamily(grid, "Longitudinal inferior (zapata)", _cfg.FootingLongitudinalBottom, leg: false, cut: false, legHint: null);
            AddFamily(grid, "Longitudinal superior (zapata)", _cfg.FootingLongitudinalTop, leg: false, cut: false, legHint: null);

            var panel = new StackPanel();
            panel.Children.Add(grid);
            panel.Children.Add(new TextBlock
            {
                Text = "Trasdos = cara del talon (vuelo mayor de zapata). El tipo de barra se busca por nombre exacto o " +
                       "parcial entre los tipos cargados en el proyecto. Baston > 0 corta las verticales a esa altura sobre la zapata.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(4, 6, 4, 0)
            });
            group.Content = panel;
            return group;
        }

        private void AddFamily(Grid grid, string name, BarFamilyCfg fam, bool leg, bool cut, string legHint)
        {
            int r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var row = new FamilyRow { Cfg = fam, Name = name };

            var label = new TextBlock { Text = name, Margin = Pad, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(label, r); Grid.SetColumn(label, 0);
            grid.Children.Add(label);

            row.Enabled = new CheckBox { IsChecked = fam.Enabled, Margin = Pad, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            Grid.SetRow(row.Enabled, r); Grid.SetColumn(row.Enabled, 1);
            grid.Children.Add(row.Enabled);

            row.Type = new ComboBox { IsEditable = true, Margin = Pad, MinWidth = 160 };
            foreach (string t in _barTypes) row.Type.Items.Add(t);
            string current = fam.BarTypeName ?? "";
            string match = _barTypes.FirstOrDefault(t => string.Equals(t, current, StringComparison.OrdinalIgnoreCase))
                        ?? _barTypes.FirstOrDefault(t => current.Length > 0 && t.IndexOf(current, StringComparison.OrdinalIgnoreCase) >= 0);
            row.Type.Text = match ?? current;
            Grid.SetRow(row.Type, r); Grid.SetColumn(row.Type, 2);
            grid.Children.Add(row.Type);

            row.Spacing = NumBox(fam.SpacingMm);
            Grid.SetRow(row.Spacing, r); Grid.SetColumn(row.Spacing, 3);
            grid.Children.Add(row.Spacing);

            row.Leg = NumBox(fam.LegMm);
            row.Leg.IsEnabled = leg;
            if (!leg) row.Leg.Text = "-";
            if (legHint != null) row.Leg.ToolTip = legHint;
            Grid.SetRow(row.Leg, r); Grid.SetColumn(row.Leg, 4);
            grid.Children.Add(row.Leg);

            row.Cut = NumBox(fam.CutLengthMm);
            row.Cut.IsEnabled = cut;
            if (!cut) row.Cut.Text = "-";
            else row.Cut.ToolTip = "0 = hasta coronacion";
            Grid.SetRow(row.Cut, r); Grid.SetColumn(row.Cut, 5);
            grid.Children.Add(row.Cut);

            // los campos numericos solo cuentan con la familia activa
            RoutedEventHandler sync = (s, e) =>
            {
                bool on = row.Enabled.IsChecked == true;
                row.Type.IsEnabled = on;
                row.Spacing.IsEnabled = on;
                row.Leg.IsEnabled = on && leg;
                row.Cut.IsEnabled = on && cut;
            };
            row.Enabled.Checked += sync;
            row.Enabled.Unchecked += sync;
            sync(null, null);

            _rows.Add(row);
        }

        private UIElement BuildCovers()
        {
            var group = new GroupBox { Header = "Recubrimientos (mm)", Padding = new Thickness(4), Margin = new Thickness(0, 0, 3, 0) };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _covStem = AddLabeled(grid, "Alzado (caras)", _cfg.CoverStemMm);
            _covStemTop = AddLabeled(grid, "Coronacion", _cfg.CoverStemTopMm);
            _covFootTop = AddLabeled(grid, "Zapata, cara superior", _cfg.CoverFootingTopMm);
            _covFootBot = AddLabeled(grid, "Zapata, cara inferior", _cfg.CoverFootingBottomMm);
            _covFootSide = AddLabeled(grid, "Zapata, laterales", _cfg.CoverFootingSideMm);
            _covEnd = AddLabeled(grid, "Extremos del tramo", _cfg.CoverEndMm);
            group.Content = grid;
            return group;
        }

        private UIElement BuildOptions()
        {
            var group = new GroupBox { Header = "Opciones de armado", Padding = new Thickness(4), Margin = new Thickness(3, 0, 0, 0) };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _band = AddLabeled(grid, "Horizontales del alzado por bandas (mm, 0 = barra a barra)", _cfg.StemHorizontalBandMm);

            AddHeading(grid, "Muros esquineros en L");

            _through = new ComboBox { Margin = Pad, HorizontalAlignment = HorizontalAlignment.Stretch };
            _through.Items.Add("Automatico: el ala de tramo recto mas largo");
            _through.Items.Add("Ala 1 (eje X de la familia)");
            _through.Items.Add("Ala 2 (eje Y de la familia)");
            _through.SelectedIndex = _cfg.CornerThroughWingIndex + 1;
            _through.ToolTip = "El ala pasante lleva sus verticales hasta la cara exterior del alzado de la otra ala y su malla de " +
                               "zapata atraviesa el bloque de esquina. Se puede cambiar elemento a elemento en la lista de arriba.";
            AddControl(grid, "Ala pasante por defecto", _through);

            var lap = new StackPanel { Orientation = Orientation.Horizontal };
            _lapDia = NumBox(_cfg.CornerLapDiameters);
            _lapMin = NumBox(_cfg.CornerLapMinMm);
            lap.Children.Add(_lapDia);
            lap.Children.Add(new TextBlock { Text = "x diametro, minimo", Margin = Pad, VerticalAlignment = VerticalAlignment.Center });
            lap.Children.Add(_lapMin);
            lap.Children.Add(new TextBlock { Text = "mm  (0 y 0 = sin pata)", Margin = Pad, VerticalAlignment = VerticalAlignment.Center });
            AddControl(grid, "Pata de solape de los horizontales en la esquina", lap);

            _mesh = new ComboBox { Margin = Pad, HorizontalAlignment = HorizontalAlignment.Stretch };
            _mesh.Items.Add("Malla completa del ala pasante (la otra ala para en la cara del bloque)");
            _mesh.Items.Add("Transversales de las dos alas cruzadas (longitudinales paran en el bloque)");
            _mesh.SelectedIndex = _cfg.CornerFootingMeshBoth ? 1 : 0;
            AddControl(grid, "Malla de zapata en el bloque de esquina", _mesh);

            var note = new TextBlock
            {
                Text = "En la esquina, los horizontales de cada ala giran en L sobre la linea de barra de la otra ala " +
                       "(exterior con exterior, interior con interior). Cada barra se comprueba contra el solido " +
                       "completo: si alguna queda fuera del hormigon, el elemento entero se deshace.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(4, 6, 4, 0)
            };
            int r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(note, r); Grid.SetColumn(note, 0); Grid.SetColumnSpan(note, 2);
            grid.Children.Add(note);

            group.Content = grid;
            return group;
        }

        private UIElement BuildButtons()
        {
            var panel = new Grid();
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var save = new Button { Content = "Guardar como valores por defecto", Padding = new Thickness(10, 4, 10, 4) };
            save.ToolTip = "Escribe estos valores en " + AppConfig.ConfigPath();
            save.Click += OnSave;
            Grid.SetColumn(save, 0);
            panel.Children.Add(save);

            int ok = _items.Count(i => i.CanBuild);
            var build = new Button
            {
                Content = "Armar " + ok + " elemento(s)",
                IsDefault = true,
                IsEnabled = ok > 0,
                Padding = new Thickness(16, 4, 16, 4),
                Margin = new Thickness(6, 0, 6, 0)
            };
            build.Click += OnBuild;
            Grid.SetColumn(build, 2);
            panel.Children.Add(build);

            var cancel = new Button { Content = "Cancelar", IsCancel = true, Padding = new Thickness(16, 4, 16, 4) };
            Grid.SetColumn(cancel, 3);
            panel.Children.Add(cancel);
            return panel;
        }

        // ------------------------------------------------------------------
        // Ayudas de construccion
        // ------------------------------------------------------------------
        private static TextBox NumBox(double value) => new TextBox
        {
            Text = Fmt(value),
            Width = 70,
            Margin = Pad,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Right
        };

        private static TextBox AddLabeled(Grid grid, string label, double value)
        {
            TextBox tb = NumBox(value);
            tb.HorizontalAlignment = HorizontalAlignment.Left;
            AddControl(grid, label, tb);
            return tb;
        }

        private static void AddControl(Grid grid, string label, UIElement control)
        {
            int r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, Margin = Pad, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(l, r); Grid.SetColumn(l, 0);
            grid.Children.Add(l);
            Grid.SetRow(control, r); Grid.SetColumn(control, 1);
            grid.Children.Add(control);
        }

        private static void AddHeading(Grid grid, string text)
        {
            int r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var h = new TextBlock { Text = text, FontWeight = FontWeights.Bold, Margin = new Thickness(4, 8, 4, 2) };
            Grid.SetRow(h, r); Grid.SetColumn(h, 0); Grid.SetColumnSpan(h, 2);
            grid.Children.Add(h);
        }

        private static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private static string Mm(double ft) => WallSection.ToMm(ft).ToString("0", CultureInfo.InvariantCulture);

        // ------------------------------------------------------------------
        // Lectura de la interfaz -> configuracion
        // ------------------------------------------------------------------
        private static bool TryNum(TextBox tb, string label, double min, List<string> errors, out double value)
        {
            string t = (tb.Text ?? "").Trim().Replace(',', '.');
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= min)
                return true;
            errors.Add(label + ": valor no valido (\"" + tb.Text + "\", minimo " + Fmt(min) + ")");
            value = 0;
            return false;
        }

        /// <summary>Vuelca los controles en _cfg. Devuelve false (y avisa) si hay valores no validos.</summary>
        private bool Collect()
        {
            var errors = new List<string>();
            double v;

            if (TryNum(_covStem, "Recubrimiento alzado", 0, errors, out v)) _cfg.CoverStemMm = v;
            if (TryNum(_covStemTop, "Recubrimiento coronacion", 0, errors, out v)) _cfg.CoverStemTopMm = v;
            if (TryNum(_covFootTop, "Recubrimiento zapata superior", 0, errors, out v)) _cfg.CoverFootingTopMm = v;
            if (TryNum(_covFootBot, "Recubrimiento zapata inferior", 0, errors, out v)) _cfg.CoverFootingBottomMm = v;
            if (TryNum(_covFootSide, "Recubrimiento zapata laterales", 0, errors, out v)) _cfg.CoverFootingSideMm = v;
            if (TryNum(_covEnd, "Recubrimiento extremos", 0, errors, out v)) _cfg.CoverEndMm = v;
            if (TryNum(_band, "Bandas de horizontales", 0, errors, out v)) _cfg.StemHorizontalBandMm = v;
            if (TryNum(_lapDia, "Solape en esquina (diametros)", 0, errors, out v)) _cfg.CornerLapDiameters = v;
            if (TryNum(_lapMin, "Solape en esquina (minimo mm)", 0, errors, out v)) _cfg.CornerLapMinMm = v;

            _cfg.CornerThroughWing = _through.SelectedIndex == 1 ? "1" : _through.SelectedIndex == 2 ? "2" : "auto";
            _cfg.CornerFootingMesh = _mesh.SelectedIndex == 1 ? "both" : "through";

            foreach (FamilyRow row in _rows)
            {
                row.Cfg.Enabled = row.Enabled.IsChecked == true;
                if (!row.Cfg.Enabled) continue;

                string type = (row.Type.Text ?? "").Trim();
                if (type.Length == 0) errors.Add(row.Name + ": elige un tipo de barra");
                else row.Cfg.BarTypeName = type;

                if (TryNum(row.Spacing, row.Name + ", separacion", 1, errors, out v)) row.Cfg.SpacingMm = v;
                if (row.Leg.IsEnabled && TryNum(row.Leg, row.Name + ", patilla", 0, errors, out v)) row.Cfg.LegMm = v;
                if (row.Cut.IsEnabled && TryNum(row.Cut, row.Name + ", baston", 0, errors, out v)) row.Cfg.CutLengthMm = v;
            }

            if (errors.Count == 0) return true;
            MessageBox.Show(this, string.Join(Environment.NewLine, errors), "Revisa los valores",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        private void OnBuild(object sender, RoutedEventArgs e)
        {
            if (!Collect()) return;
            Result = _cfg;
            DialogResult = true;
            Close();
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            if (!Collect()) return;
            try
            {
                _cfg.Save();
                MessageBox.Show(this, "Valores guardados en:" + Environment.NewLine + AppConfig.ConfigPath() +
                                Environment.NewLine + Environment.NewLine +
                                "Ojo: compilar en Debug vuelve a copiar el config.json del proyecto sobre este archivo.",
                                "Guardado", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "No se pudo guardar: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
