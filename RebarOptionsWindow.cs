using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RetainingWallRebar
{
    /// <summary>
    /// Ventana previa al armado: muestra que se ha detectado en cada elemento seleccionado
    /// (tramo recto, esquinero en L o rechazado con el motivo) y deja elegir a mano el
    /// armado (familias activas, tipo de barra, separaciones, patillas, recubrimientos,
    /// reglas de esquina) y el reparto de los horizontales del alzado por tramos de
    /// altura, con un esquema del muro marcado que se redibuja con cada cambio.
    /// Los valores iniciales vienen de config.json y se pueden guardar como nuevos
    /// valores por defecto. Construida en codigo (sin XAML) para no depender del
    /// compilador de XAML.
    /// </summary>
    public sealed class RebarOptionsWindow : Window
    {
        private readonly AppConfig _cfg;
        private readonly IList<string> _barTypes;
        private readonly IDictionary<string, double> _diametersMm;
        private readonly IList<HostAnalysis> _items;

        /// <summary>Configuracion final si el usuario pulso "Armar"; null si cancelo.</summary>
        public AppConfig Result { get; private set; }

        private sealed class FamilyRow
        {
            public Func<AppConfig, BarFamilyCfg> Select;
            public string Name;
            public bool HasLeg, HasCut;
            public CheckBox Enabled;
            public ComboBox Type;
            public TextBox Spacing, Leg, Cut;
        }

        private sealed class ZoneRow
        {
            public int Index;
            public TextBox Top;
            public ComboBox BackType, FrontType;
            public TextBox BackSpacing, FrontSpacing;
            public TextBlock Height, Bars;
            public List<UIElement> Cells = new List<UIElement>();
        }

        private sealed class ReinfRow
        {
            public FootingReinfCfg Cfg;
            public ComboBox Layer, Position, Type;
            public TextBox Spacing, L1, L2;
        }

        private readonly List<FamilyRow> _rows = new List<FamilyRow>();
        private readonly List<FootingReinfCfg> _reinfStore = new List<FootingReinfCfg>();
        private readonly List<ReinfRow> _reinfRows = new List<ReinfRow>();
        private Grid _reinfGrid;
        private TextBlock _reinfMessage;
        private TextBox _covStem, _covStemTop, _covFootTop, _covFootBot, _covFootSide, _covEnd;
        private TextBox _band, _lapDia, _lapMin;
        private ComboBox _through, _mesh;

        // --- tramos de horizontales ---
        /// <summary>Siempre 3 tramos guardados, para que al pasar de 3 a 1 y volver no se pierdan los valores.</summary>
        private readonly StemZoneCfg[] _zoneStore = new StemZoneCfg[3];
        private int _zoneCount;
        private RadioButton _modeAuto, _modeManual;
        private readonly RadioButton[] _countButtons = new RadioButton[3];
        private CheckBox _backOn, _frontOn, _sameFaces;
        private Grid _zoneGrid;
        private readonly List<ZoneRow> _zoneRows = new List<ZoneRow>();
        private TextBlock _zoneMessage;
        private StemPreview _preview;
        private TextBlock _previewCaption;

        private HostAnalysis _selected;
        private readonly Dictionary<HostAnalysis, Border> _itemRows = new Dictionary<HostAnalysis, Border>();
        private bool _building = true;
        private bool _refreshing;

        private static readonly Thickness Pad = new Thickness(4, 2, 4, 2);
        private static readonly Brush SelectedBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0xE8, 0xF6));

        public RebarOptionsWindow(AppConfig cfg, IList<string> barTypes, IDictionary<string, double> diametersMm, IList<HostAnalysis> items)
        {
            _cfg = cfg;
            _cfg.Normalize();
            _barTypes = barTypes;
            _diametersMm = diametersMm;
            _items = items;

            foreach (FootingReinfCfg r in _cfg.FootingReinforcements) _reinfStore.Add(r.Clone());

            List<StemZoneCfg> zones = _cfg.StemHorizontalZones;
            _zoneCount = Math.Max(1, Math.Min(3, zones.Count));
            for (int i = 0; i < 3; i++)
                _zoneStore[i] = (i < zones.Count ? zones[i] : zones[zones.Count - 1]).Clone();

            Title = "Armar muros de contencion";
            Width = 1180;
            Height = 820;
            MinWidth = 960;
            MinHeight = 600;
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

            var body = new StackPanel();
            body.Children.Add(BuildZones());
            body.Children.Add(BuildFamilies());
            body.Children.Add(BuildReinforcements());

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
                Header = "Elementos seleccionados: " + _items.Count + " (" + ok + " armables). Haz clic en uno para verlo en el esquema.",
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
                Content = panel,
                MaxHeight = 150,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            return group;
        }

        private void SelectItem(HostAnalysis item)
        {
            _selected = item;
            foreach (var kv in _itemRows)
                kv.Value.Background = kv.Key == item ? SelectedBrush : Brushes.Transparent;
        }

        // ------------------------------------------------------------------
        // Horizontales del alzado por tramos
        // ------------------------------------------------------------------
        private UIElement BuildZones()
        {
            var group = new GroupBox { Header = "Horizontales del alzado: reparto por tramos de altura", Padding = new Thickness(4) };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });

            // --- controles ---
            var left = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };

            var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            modeRow.Children.Add(new TextBlock { Text = "Reparto:", FontWeight = FontWeights.SemiBold, Margin = Pad, VerticalAlignment = VerticalAlignment.Center, Width = 70 });
            _modeAuto = new RadioButton { Content = "Automatico (partes iguales)", GroupName = "mode", Margin = Pad, VerticalAlignment = VerticalAlignment.Center, IsChecked = !_cfg.StemZoneModeManual };
            _modeManual = new RadioButton { Content = "Editable (cotas en metros sobre la zapata)", GroupName = "mode", Margin = new Thickness(14, 2, 4, 2), VerticalAlignment = VerticalAlignment.Center, IsChecked = _cfg.StemZoneModeManual };
            _modeAuto.Checked += (s, e) => OnModeChanged();
            _modeManual.Checked += (s, e) => OnModeChanged();
            modeRow.Children.Add(_modeAuto);
            modeRow.Children.Add(_modeManual);
            left.Children.Add(modeRow);

            var countRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            countRow.Children.Add(new TextBlock { Text = "Tramos:", FontWeight = FontWeights.SemiBold, Margin = Pad, VerticalAlignment = VerticalAlignment.Center, Width = 70 });
            string[] countText = { "1 tramo (uniforme)", "2 tramos (mitades)", "3 tramos (tercios)" };
            for (int i = 0; i < 3; i++)
            {
                var rb = new RadioButton { Content = countText[i], GroupName = "count", Margin = new Thickness(i == 0 ? 4 : 14, 2, 4, 2), VerticalAlignment = VerticalAlignment.Center, IsChecked = _zoneCount == i + 1 };
                int n = i + 1;
                rb.Checked += (s, e) => OnZoneCountChanged(n);
                _countButtons[i] = rb;
                countRow.Children.Add(rb);
            }
            left.Children.Add(countRow);

            var facesRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 4) };
            facesRow.Children.Add(new TextBlock { Text = "Caras:", FontWeight = FontWeights.SemiBold, Margin = Pad, VerticalAlignment = VerticalAlignment.Center, Width = 70 });
            _backOn = new CheckBox { Content = "Trasdos activo", IsChecked = _cfg.StemHorizontalBackEnabled, Margin = Pad, VerticalAlignment = VerticalAlignment.Center };
            _frontOn = new CheckBox { Content = "Intrados activo", IsChecked = _cfg.StemHorizontalFrontEnabled, Margin = new Thickness(14, 2, 4, 2), VerticalAlignment = VerticalAlignment.Center };
            _sameFaces = new CheckBox { Content = "Mismo armado en las dos caras", IsChecked = _zoneStore.Take(_zoneCount).All(z => z.SameBothFaces), Margin = new Thickness(14, 2, 4, 2), VerticalAlignment = VerticalAlignment.Center };
            RoutedEventHandler facesChanged = (s, e) => { UpdateZoneRowState(); Refresh(); };
            foreach (CheckBox cb in new[] { _backOn, _frontOn, _sameFaces }) { cb.Checked += facesChanged; cb.Unchecked += facesChanged; }
            facesRow.Children.Add(_backOn);
            facesRow.Children.Add(_frontOn);
            facesRow.Children.Add(_sameFaces);
            left.Children.Add(facesRow);

            _zoneGrid = new Grid();
            for (int c = 0; c < 8; c++)
                _zoneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _zoneGrid.ColumnDefinitions[3].Width = new GridLength(1, GridUnitType.Star);
            _zoneGrid.ColumnDefinitions[5].Width = new GridLength(1, GridUnitType.Star);
            left.Children.Add(_zoneGrid);
            RebuildZoneTable();

            _zoneMessage = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 4, 4, 0), Foreground = Brushes.Firebrick };
            left.Children.Add(_zoneMessage);
            left.Children.Add(new TextBlock
            {
                Text = "Los tramos se numeran de abajo arriba. En cada tramo la primera barra va a media separacion de su " +
                       "limite inferior; la ultima del tramo superior respeta el recubrimiento de coronacion. Las cotas del " +
                       "modo editable se miden desde la cara superior de la zapata y se aplican a todos los muros seleccionados; " +
                       "un tramo que quede por encima de la coronacion de un muro se omite en ese muro.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(4, 6, 4, 0)
            });
            Grid.SetColumn(left, 0);
            grid.Children.Add(left);

            // --- esquema ---
            var right = new Grid();
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _previewCaption = new TextBlock { Text = "Esquema", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2), TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetRow(_previewCaption, 0);
            right.Children.Add(_previewCaption);
            _preview = new StemPreview { MinHeight = 320 };
            var frame = new Border { Child = _preview, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Background = Brushes.White };
            Grid.SetRow(frame, 1);
            right.Children.Add(frame);
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);

            group.Content = grid;
            return group;
        }

        private void RebuildZoneTable()
        {
            _zoneGrid.Children.Clear();
            _zoneGrid.RowDefinitions.Clear();
            _zoneRows.Clear();

            string[] headers = { "Tramo", "Cota superior (m)", "Altura (m)", "Trasdos: tipo de barra", "sep. (mm)", "Intrados: tipo de barra", "sep. (mm)", "Barras / cara" };
            _zoneGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < headers.Length; c++)
            {
                var h = new TextBlock { Text = headers[c], FontWeight = FontWeights.Bold, Margin = Pad };
                Grid.SetRow(h, 0); Grid.SetColumn(h, c);
                _zoneGrid.Children.Add(h);
            }

            // el tramo superior en la primera fila, igual que en el esquema
            for (int i = _zoneCount - 1; i >= 0; i--)
            {
                StemZoneCfg z = _zoneStore[i];
                int r = _zoneGrid.RowDefinitions.Count;
                _zoneGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var row = new ZoneRow { Index = i };
                Brush brush = StemPreview.ZoneBrushes[i % StemPreview.ZoneBrushes.Length];

                string pos = _zoneCount == 1 ? "unico" : i == _zoneCount - 1 ? "superior" : i == 0 ? "inferior" : "intermedio";
                var label = new StackPanel { Orientation = Orientation.Horizontal, Margin = Pad, VerticalAlignment = VerticalAlignment.Center };
                label.Children.Add(new Border { Width = 12, Height = 12, Background = brush, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                label.Children.Add(new TextBlock { Text = "Tramo " + (i + 1) + " (" + pos + ")", VerticalAlignment = VerticalAlignment.Center });
                Add(row, label, r, 0);

                row.Top = NumBox(z.TopMm > 0 ? z.TopMm / 1000.0 : 0);
                row.Top.ToolTip = "Cota superior del tramo sobre la cara superior de la zapata";
                if (i == _zoneCount - 1) { row.Top.Text = "coronacion"; row.Top.IsReadOnly = true; row.Top.Foreground = Brushes.DimGray; }
                row.Top.TextChanged += (s, e) => Refresh();
                Add(row, row.Top, r, 1);

                row.Height = new TextBlock { Text = "-", Margin = Pad, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 50 };
                Add(row, row.Height, r, 2);

                row.BackType = TypeBox(z.BackBarTypeName);
                Add(row, row.BackType, r, 3);
                row.BackSpacing = NumBox(z.BackSpacingMm);
                row.BackSpacing.TextChanged += (s, e) => Refresh();
                Add(row, row.BackSpacing, r, 4);

                row.FrontType = TypeBox(z.FrontBarTypeName);
                Add(row, row.FrontType, r, 5);
                row.FrontSpacing = NumBox(z.FrontSpacingMm);
                row.FrontSpacing.TextChanged += (s, e) => Refresh();
                Add(row, row.FrontSpacing, r, 6);

                row.Bars = new TextBlock { Text = "-", Margin = Pad, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 60 };
                Add(row, row.Bars, r, 7);

                int zi = i;
                foreach (UIElement cell in row.Cells)
                {
                    cell.MouseEnter += (s, e) => _preview.HoverZone = zi;
                    cell.MouseLeave += (s, e) => _preview.HoverZone = -1;
                }
                _zoneRows.Add(row);
            }
            UpdateZoneRowState();
        }

        private void Add(ZoneRow row, UIElement el, int r, int c)
        {
            Grid.SetRow(el, r); Grid.SetColumn(el, c);
            _zoneGrid.Children.Add(el);
            row.Cells.Add(el);
        }

        private ComboBox TypeBox(string current)
        {
            var cb = new ComboBox { IsEditable = true, Margin = Pad, MinWidth = 120 };
            foreach (string t in _barTypes) cb.Items.Add(t);
            cb.Text = RebarGenerator.MatchName(_barTypes, current) ?? current ?? "";
            cb.SelectionChanged += (s, e) => Dispatcher.BeginInvoke(new Action(Refresh));
            cb.LostFocus += (s, e) => Refresh();
            return cb;
        }

        /// <summary>Activa o desactiva los controles de cada tramo segun modo, caras activas y "mismo armado".</summary>
        private void UpdateZoneRowState()
        {
            bool manual = _modeManual.IsChecked == true;
            bool back = _backOn.IsChecked == true, front = _frontOn.IsChecked == true;
            bool same = _sameFaces.IsChecked == true;
            foreach (ZoneRow row in _zoneRows)
            {
                bool isTop = row.Index == _zoneCount - 1;
                row.Top.IsReadOnly = !manual || isTop;
                row.Top.Foreground = row.Top.IsReadOnly ? Brushes.DimGray : Brushes.Black;
                row.BackType.IsEnabled = back;
                row.BackSpacing.IsEnabled = back;
                row.FrontType.IsEnabled = front && !same;
                row.FrontSpacing.IsEnabled = front && !same;
            }
        }

        private void OnModeChanged()
        {
            if (_building) return;
            bool manual = _modeManual.IsChecked == true;
            if (manual)
            {
                // al pasar a editable se recuperan las cotas guardadas; si no hay, arrancan
                // en el reparto automatico del muro marcado
                WallSection s = SelectedSection();
                double h = s != null ? (s.LenV - s.FootingTop) * 0.3048 : 0;
                _refreshing = true;
                foreach (ZoneRow row in _zoneRows)
                {
                    if (row.Index == _zoneCount - 1) continue;
                    double stored = _zoneStore[row.Index].TopMm;
                    row.Top.Text = stored > 0 ? Fmt(stored / 1000.0) : h > 0 ? Fmt(h * (row.Index + 1) / _zoneCount) : "";
                }
                _refreshing = false;
            }
            UpdateZoneRowState();
            Refresh();
        }

        private void OnZoneCountChanged(int n)
        {
            if (_building || n == _zoneCount) return;
            ReadZoneRows(null);
            // los tramos nuevos heredan el armado del que hasta ahora era el superior
            for (int i = _zoneCount; i < n; i++)
            {
                StemZoneCfg src = _zoneStore[_zoneCount - 1];
                _zoneStore[i].BackBarTypeName = src.BackBarTypeName;
                _zoneStore[i].BackSpacingMm = src.BackSpacingMm;
                _zoneStore[i].FrontBarTypeName = src.FrontBarTypeName;
                _zoneStore[i].FrontSpacingMm = src.FrontSpacingMm;
                _zoneStore[i].TopMm = 0;
            }
            _zoneCount = n;
            _building = true;
            RebuildZoneTable();
            _building = false;
            if (_modeManual.IsChecked == true) OnModeChanged(); else Refresh();
        }

        /// <summary>Vuelca la tabla de tramos en _zoneStore. errors puede ser null (lectura tolerante).</summary>
        private void ReadZoneRows(List<string> errors)
        {
            bool manual = _modeManual.IsChecked == true;
            bool same = _sameFaces.IsChecked == true;
            double prevTop = 0;
            foreach (ZoneRow row in _zoneRows.OrderBy(r => r.Index))
            {
                StemZoneCfg z = _zoneStore[row.Index];
                z.SameBothFaces = same;
                bool isTop = row.Index == _zoneCount - 1;
                string name = "Tramo " + (row.Index + 1);

                if (manual && !isTop)
                {
                    if (TryParse(row.Top.Text, out double m) && m > 0)
                    {
                        if (m * 1000 <= prevTop + 1e-6) errors?.Add(name + ": la cota superior tiene que ser mayor que la del tramo anterior");
                        z.TopMm = m * 1000;
                        prevTop = z.TopMm;
                    }
                    else errors?.Add(name + ": cota superior no valida (\"" + row.Top.Text + "\")");
                }

                string bt = (row.BackType.Text ?? "").Trim();
                if (bt.Length > 0) z.BackBarTypeName = bt;
                else if (_backOn.IsChecked == true) errors?.Add(name + ": elige el tipo de barra del trasdos");
                if (TryParse(row.BackSpacing.Text, out double bs) && bs >= 1) z.BackSpacingMm = bs;
                else if (_backOn.IsChecked == true) errors?.Add(name + ": separacion del trasdos no valida");

                if (same)
                {
                    z.FrontBarTypeName = z.BackBarTypeName;
                    z.FrontSpacingMm = z.BackSpacingMm;
                }
                else
                {
                    string ft = (row.FrontType.Text ?? "").Trim();
                    if (ft.Length > 0) z.FrontBarTypeName = ft;
                    else if (_frontOn.IsChecked == true) errors?.Add(name + ": elige el tipo de barra del intrados");
                    if (TryParse(row.FrontSpacing.Text, out double fs) && fs >= 1) z.FrontSpacingMm = fs;
                    else if (_frontOn.IsChecked == true) errors?.Add(name + ": separacion del intrados no valida");
                }
            }
        }

        private WallSection SelectedSection()
        {
            if (_selected == null || !_selected.CanBuild) return null;
            return _selected.Straight ?? _selected.Corner.Wings[0];
        }

        private double DiameterFt(string name)
        {
            if (_barTypes.Count == 0) return 0;
            string match = RebarGenerator.MatchName(_barTypes, name) ?? _barTypes[0];
            return _diametersMm.TryGetValue(match, out double mm) ? WallSection.Mm(mm) : 0;
        }

        /// <summary>Recalcula el reparto con lo que hay en pantalla, actualiza la tabla y redibuja el esquema.</summary>
        private void Refresh()
        {
            if (_building || _refreshing) return;
            _refreshing = true;
            try { RefreshCore(); }
            finally { _refreshing = false; }
        }

        private void RefreshCore()
        {
            var scratch = _cfg.Clone();
            ReadUi(scratch, null);

            WallSection s = SelectedSection();
            if (s == null)
            {
                foreach (ZoneRow row in _zoneRows) { row.Height.Text = "-"; row.Bars.Text = "-"; }
                _zoneMessage.Text = "";
                if (_reinfMessage != null) _reinfMessage.Text = "";
                _previewCaption.Text = "Esquema: sin elemento armable";
                _preview.Clear("Sin elemento armable");
                return;
            }

            ZoneLayout layout = StemZones.Resolve(s, scratch, DiameterFt, false);
            var msgs = new List<string>();
            if (layout.Error != null) msgs.Add(layout.Error);
            msgs.AddRange(layout.Warnings);
            _zoneMessage.Text = string.Join(Environment.NewLine, msgs);

            foreach (ZoneRow row in _zoneRows)
            {
                ResolvedZone z = layout.Zones.FirstOrDefault(x => x.Index == row.Index);
                if (z == null) { row.Height.Text = "-"; row.Bars.Text = "-"; continue; }
                row.Height.Text = Fmt((z.VTo - z.VFrom) * 0.3048);
                if (_modeAuto.IsChecked == true && !z.IsTop) row.Top.Text = Fmt((z.VTo - s.FootingTop) * 0.3048);
                string b = z.Back != null ? z.Back.Heights.Count.ToString() : "-";
                string f = z.Front != null ? z.Front.Heights.Count.ToString() : "-";
                row.Bars.Text = (z.Cfg.SameBothFaces && z.Back != null && z.Front != null) ? b : b + " / " + f;
            }

            var rmsgs = new List<string>();
            foreach (FootingReinfCfg r in scratch.FootingReinforcements)
            {
                SectionBars.Reinforcement(s, scratch, r, DiameterFt, out string warn);
                if (warn != null) rmsgs.Add(warn);
            }
            if (_reinfMessage != null) _reinfMessage.Text = string.Join(Environment.NewLine, rmsgs);

            _previewCaption.Text = "Esquema: " + _selected.Tag.Trim() + (_selected.Corner != null ? " (ala 1)" : "");
            _preview.Show(s, scratch, layout, DiameterFt);
        }

        // ------------------------------------------------------------------
        // Resto de familias, recubrimientos y opciones
        // ------------------------------------------------------------------
        private UIElement BuildFamilies()
        {
            var group = new GroupBox { Header = "Verticales del alzado y zapata", Padding = new Thickness(4), Margin = new Thickness(0, 6, 0, 0) };
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

            string legV = "Patilla en zapata: cruza bajo la pantalla hacia el lado contrario y sobresale esta longitud de la cara opuesta del alzado";
            AddFamily(grid, "Vertical trasdos (alzado)", c => c.StemVerticalBack, leg: true, cut: true, legHint: legV);
            AddFamily(grid, "Vertical intrados (alzado)", c => c.StemVerticalFront, leg: true, cut: true, legHint: legV);
            AddFamily(grid, "Transversal inferior (zapata)", c => c.FootingTransverseBottom, leg: true, cut: false, legHint: "Pata vertical en los extremos (hacia arriba)");
            AddFamily(grid, "Transversal superior (zapata)", c => c.FootingTransverseTop, leg: true, cut: false, legHint: "Pata vertical en los extremos (hacia abajo, por dentro de las de la inferior)");
            AddFamily(grid, "Longitudinal inferior (zapata)", c => c.FootingLongitudinalBottom, leg: false, cut: false, legHint: null);
            AddFamily(grid, "Longitudinal superior (zapata)", c => c.FootingLongitudinalTop, leg: false, cut: false, legHint: null);

            var panel = new StackPanel();
            panel.Children.Add(grid);
            panel.Children.Add(new TextBlock
            {
                Text = "Trasdos = cara del talon (vuelo mayor de zapata). El tipo de barra se busca por nombre exacto o " +
                       "parcial entre los tipos cargados en el proyecto. Baston > 0 corta las verticales a esa altura sobre la zapata. " +
                       "La patilla de las verticales se apoya sobre la parrilla inferior, cruza bajo la pantalla y sobresale la " +
                       "longitud indicada de la cara opuesta; la del intrados va apilada sobre la del trasdos.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(4, 6, 4, 0)
            });
            group.Content = panel;
            return group;
        }

        private void AddFamily(Grid grid, string name, Func<AppConfig, BarFamilyCfg> select, bool leg, bool cut, string legHint)
        {
            int r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            BarFamilyCfg fam = select(_cfg);
            var row = new FamilyRow { Select = select, Name = name, HasLeg = leg, HasCut = cut };

            var label = new TextBlock { Text = name, Margin = Pad, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(label, r); Grid.SetColumn(label, 0);
            grid.Children.Add(label);

            row.Enabled = new CheckBox { IsChecked = fam.Enabled, Margin = Pad, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            Grid.SetRow(row.Enabled, r); Grid.SetColumn(row.Enabled, 1);
            grid.Children.Add(row.Enabled);

            row.Type = TypeBox(fam.BarTypeName);
            row.Type.MinWidth = 160;
            Grid.SetRow(row.Type, r); Grid.SetColumn(row.Type, 2);
            grid.Children.Add(row.Type);

            row.Spacing = NumBox(fam.SpacingMm);
            Grid.SetRow(row.Spacing, r); Grid.SetColumn(row.Spacing, 3);
            grid.Children.Add(row.Spacing);

            row.Leg = NumBox(fam.LegMm);
            if (!leg) row.Leg.Text = "-";
            if (legHint != null) row.Leg.ToolTip = legHint;
            Grid.SetRow(row.Leg, r); Grid.SetColumn(row.Leg, 4);
            grid.Children.Add(row.Leg);

            row.Cut = NumBox(fam.CutLengthMm);
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

            // el esquema dibuja tambien verticales y zapata: se redibuja con cualquier cambio
            row.Enabled.Checked += (s, e) => Refresh();
            row.Enabled.Unchecked += (s, e) => Refresh();
            row.Spacing.TextChanged += (s, e) => Refresh();
            row.Leg.TextChanged += (s, e) => Refresh();
            row.Cut.TextChanged += (s, e) => Refresh();

            _rows.Add(row);
        }

        // ------------------------------------------------------------------
        // Refuerzos transversales cortos de zapata
        // ------------------------------------------------------------------
        private UIElement BuildReinforcements()
        {
            var group = new GroupBox { Header = "Refuerzos transversales de zapata (barras cortas)", Padding = new Thickness(4), Margin = new Thickness(0, 6, 0, 0) };
            var panel = new StackPanel();

            _reinfGrid = new Grid();
            for (int c = 0; c < 7; c++)
                _reinfGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _reinfGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
            panel.Children.Add(_reinfGrid);
            RebuildReinfTable();

            var add = new Button { Content = "Anadir refuerzo", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(4, 6, 4, 2), HorizontalAlignment = HorizontalAlignment.Left };
            add.Click += (s, e) =>
            {
                ReadReinfRows(null);
                var last = _reinfStore.Count > 0 ? _reinfStore[_reinfStore.Count - 1].Clone() : new FootingReinfCfg();
                if (_reinfStore.Count == 0) last.BarTypeName = _cfg.FootingTransverseTop.BarTypeName;
                _reinfStore.Add(last);
                _building = true; RebuildReinfTable(); _building = false;
                Refresh();
            };
            panel.Children.Add(add);

            _reinfMessage = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 2, 4, 0), Foreground = Brushes.Firebrick };
            panel.Children.Add(_reinfMessage);
            panel.Children.Add(new TextBlock
            {
                Text = "Barras rectas en la misma capa que la transversal superior o inferior, intercaladas media separacion con ella. " +
                       "Puntera y talon: longitud medida desde el borde de la zapata hacia dentro (puede pasar bajo la pantalla). " +
                       "Centro: longitud hacia la puntera y hacia el talon medidas desde el eje de la pantalla en su base. " +
                       "Con una separacion distinta a la de la transversal alguna barra puede coincidir con otra.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(4, 4, 4, 0)
            });
            group.Content = panel;
            return group;
        }

        private void RebuildReinfTable()
        {
            _reinfGrid.Children.Clear();
            _reinfGrid.RowDefinitions.Clear();
            _reinfRows.Clear();

            string[] headers = { "Capa", "Posicion", "Tipo de barra", "Separacion (mm)", "Longitud 1 (mm)", "Longitud 2 (mm)", "" };
            _reinfGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < headers.Length; c++)
            {
                var h = new TextBlock { Text = headers[c], FontWeight = FontWeights.Bold, Margin = Pad };
                Grid.SetRow(h, 0); Grid.SetColumn(h, c);
                _reinfGrid.Children.Add(h);
            }
            if (_reinfStore.Count == 0)
            {
                _reinfGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var none = new TextBlock { Text = "Sin refuerzos. Pulsa \"Anadir refuerzo\" para crear uno.", Foreground = Brushes.DimGray, Margin = Pad };
                Grid.SetRow(none, 1); Grid.SetColumn(none, 0); Grid.SetColumnSpan(none, 7);
                _reinfGrid.Children.Add(none);
            }

            foreach (FootingReinfCfg cfg in _reinfStore)
            {
                int r = _reinfGrid.RowDefinitions.Count;
                _reinfGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var row = new ReinfRow { Cfg = cfg };

                row.Layer = new ComboBox { Margin = Pad, MinWidth = 90 };
                row.Layer.Items.Add("Superior");
                row.Layer.Items.Add("Inferior");
                row.Layer.SelectedIndex = cfg.Top ? 0 : 1;
                row.Layer.SelectionChanged += (s, e) => Refresh();
                Put(row.Layer, r, 0);

                row.Position = new ComboBox { Margin = Pad, MinWidth = 90 };
                row.Position.Items.Add("Puntera");
                row.Position.Items.Add("Talon");
                row.Position.Items.Add("Centro");
                row.Position.SelectedIndex = cfg.IsToe ? 0 : cfg.IsHeel ? 1 : 2;
                Put(row.Position, r, 1);

                row.Type = TypeBox(cfg.BarTypeName);
                Put(row.Type, r, 2);

                row.Spacing = NumBox(cfg.SpacingMm);
                row.Spacing.TextChanged += (s, e) => Refresh();
                Put(row.Spacing, r, 3);

                row.L1 = NumBox(cfg.IsCenter ? cfg.ToeLengthMm : cfg.LengthMm);
                row.L1.TextChanged += (s, e) => Refresh();
                Put(row.L1, r, 4);

                row.L2 = NumBox(cfg.HeelLengthMm);
                row.L2.TextChanged += (s, e) => Refresh();
                Put(row.L2, r, 5);

                var remove = new Button { Content = "Quitar", Padding = new Thickness(8, 2, 8, 2), Margin = Pad };
                FootingReinfCfg captured = cfg;
                remove.Click += (s, e) =>
                {
                    ReadReinfRows(null);
                    _reinfStore.Remove(captured);
                    _building = true; RebuildReinfTable(); _building = false;
                    Refresh();
                };
                Put(remove, r, 6);

                ReinfRow rowRef = row;
                row.Position.SelectionChanged += (s, e) =>
                {
                    // al cambiar de posicion, las cajas de longitud cambian de significado
                    bool center = rowRef.Position.SelectedIndex == 2;
                    FootingReinfCfg c = rowRef.Cfg;
                    rowRef.L1.Text = Fmt(center ? c.ToeLengthMm : c.LengthMm);
                    UpdateReinfRowState(rowRef);
                    Refresh();
                };
                UpdateReinfRowState(row);
                _reinfRows.Add(row);
            }
        }

        private void Put(UIElement el, int r, int c)
        {
            Grid.SetRow(el, r); Grid.SetColumn(el, c);
            _reinfGrid.Children.Add(el);
        }

        private static void UpdateReinfRowState(ReinfRow row)
        {
            bool center = row.Position.SelectedIndex == 2;
            row.L2.IsEnabled = center;
            row.L1.ToolTip = center ? "Hacia la puntera, desde el eje de la pantalla en su base" : "Desde el borde de la zapata hacia dentro";
            row.L2.ToolTip = center ? "Hacia el talon, desde el eje de la pantalla en su base" : "Solo en posicion Centro";
        }

        /// <summary>Vuelca la tabla de refuerzos en _reinfStore. errors puede ser null (lectura tolerante).</summary>
        private void ReadReinfRows(List<string> errors)
        {
            int i = 0;
            foreach (ReinfRow row in _reinfRows)
            {
                i++;
                FootingReinfCfg c = row.Cfg;
                string name = "Refuerzo " + i;
                c.Top = row.Layer.SelectedIndex == 0;
                c.Position = row.Position.SelectedIndex == 0 ? "toe" : row.Position.SelectedIndex == 1 ? "heel" : "center";

                string type = (row.Type.Text ?? "").Trim();
                if (type.Length > 0) c.BarTypeName = type;
                else errors?.Add(name + ": elige un tipo de barra");
                if (TryParse(row.Spacing.Text, out double sp) && sp >= 1) c.SpacingMm = sp;
                else errors?.Add(name + ": separacion no valida");

                bool okL1 = TryParse(row.L1.Text, out double l1) && l1 > 0;
                if (!okL1) errors?.Add(name + ": longitud 1 no valida");
                if (c.IsCenter)
                {
                    if (okL1) c.ToeLengthMm = l1;
                    if (TryParse(row.L2.Text, out double l2) && l2 > 0) c.HeelLengthMm = l2;
                    else errors?.Add(name + ": longitud 2 no valida");
                }
                else if (okL1) c.LengthMm = l1;
            }
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
            foreach (TextBox tb in new[] { _covStem, _covStemTop, _covFootTop, _covFootBot, _covFootSide })
                tb.TextChanged += (s, e) => Refresh();
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
            _band.ToolTip = "Con bandas, cada tramo se agrupa en arrays de esa altura: menos elementos, pero las barras de cada " +
                            "banda quedan equiespaciadas dentro de ella y no exactamente donde las dibuja el esquema.";

            AddHeading(grid, "Muros esquineros en L");

            _through = new ComboBox { Margin = Pad, HorizontalAlignment = HorizontalAlignment.Stretch };
            _through.Items.Add("Automatico: el ala de tramo recto mas largo");
            _through.Items.Add("Ala 1 (eje X de la familia)");
            _through.Items.Add("Ala 2 (eje Y de la familia)");
            _through.SelectedIndex = _cfg.CornerThroughWingIndex + 1;
            _through.ToolTip = "En un esquinero las dos alas se solapan en la esquina y una de ellas manda en ese trozo comun: " +
                               "la pasante lleva sus verticales hasta la cara exterior del alzado de la otra ala (ocupa la columna de " +
                               "esquina) y su malla de zapata atraviesa el bloque de esquina; la otra ala para justo antes. " +
                               "Los horizontales de las dos alas giran la esquina igual. Se puede cambiar elemento a elemento en la lista de arriba.";
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
            _mesh.Items.Add("Malla del ala pasante");
            _mesh.Items.Add("Transversales de las dos alas cruzadas");
            _mesh.ToolTip = "El bloque de esquina es el trozo de zapata donde se cruzan las dos alas.\n" +
                            "- Malla del ala pasante: ahi va solo la malla (transversales y longitudinales) del ala pasante; " +
                            "la otra ala para sus barras de zapata en el borde del bloque.\n" +
                            "- Transversales de las dos alas cruzadas: las transversales de ambas alas atraviesan el bloque en " +
                            "dos capas distintas; las longitudinales de ambas paran en el borde.";
            _mesh.SelectedIndex = _cfg.CornerFootingMeshBoth ? 1 : 0;
            AddControl(grid, "Malla de zapata en el bloque de esquina", _mesh);

            var note = new TextBlock
            {
                Text = "Solo para muros esquineros. En la esquina, los horizontales de cada ala giran en L sobre la linea " +
                       "de barra de la otra ala (exterior con exterior, interior con interior). Pasa el raton por cada opcion " +
                       "para ver que hace. Cada barra se comprueba contra el solido completo: si alguna queda fuera del " +
                       "hormigon, el elemento entero se deshace.",
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

        private static bool TryParse(string text, out double value)
        {
            string t = (text ?? "").Trim().Replace(',', '.');
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        // ------------------------------------------------------------------
        // Lectura de la interfaz -> configuracion
        // ------------------------------------------------------------------
        private static bool TryNum(TextBox tb, string label, double min, List<string> errors, out double value)
        {
            if (TryParse(tb.Text, out value) && value >= min) return true;
            errors?.Add(label + ": valor no valido (\"" + tb.Text + "\", minimo " + Fmt(min) + ")");
            value = 0;
            return false;
        }

        /// <summary>
        /// Vuelca los controles en target. Con errors = null la lectura es tolerante (para el
        /// esquema): los valores no validos se dejan como estaban.
        /// </summary>
        private void ReadUi(AppConfig target, List<string> errors)
        {
            double v;
            if (TryNum(_covStem, "Recubrimiento alzado", 0, errors, out v)) target.CoverStemMm = v;
            if (TryNum(_covStemTop, "Recubrimiento coronacion", 0, errors, out v)) target.CoverStemTopMm = v;
            if (TryNum(_covFootTop, "Recubrimiento zapata superior", 0, errors, out v)) target.CoverFootingTopMm = v;
            if (TryNum(_covFootBot, "Recubrimiento zapata inferior", 0, errors, out v)) target.CoverFootingBottomMm = v;
            if (TryNum(_covFootSide, "Recubrimiento zapata laterales", 0, errors, out v)) target.CoverFootingSideMm = v;
            if (TryNum(_covEnd, "Recubrimiento extremos", 0, errors, out v)) target.CoverEndMm = v;
            if (TryNum(_band, "Bandas de horizontales", 0, errors, out v)) target.StemHorizontalBandMm = v;
            if (TryNum(_lapDia, "Solape en esquina (diametros)", 0, errors, out v)) target.CornerLapDiameters = v;
            if (TryNum(_lapMin, "Solape en esquina (minimo mm)", 0, errors, out v)) target.CornerLapMinMm = v;

            target.CornerThroughWing = _through.SelectedIndex == 1 ? "1" : _through.SelectedIndex == 2 ? "2" : "auto";
            target.CornerFootingMesh = _mesh.SelectedIndex == 1 ? "both" : "through";

            foreach (FamilyRow row in _rows)
            {
                BarFamilyCfg fam = row.Select(target);
                fam.Enabled = row.Enabled.IsChecked == true;
                if (!fam.Enabled) continue;

                string type = (row.Type.Text ?? "").Trim();
                if (type.Length == 0) errors?.Add(row.Name + ": elige un tipo de barra");
                else fam.BarTypeName = type;

                if (TryNum(row.Spacing, row.Name + ", separacion", 1, errors, out v)) fam.SpacingMm = v;
                if (row.HasLeg && TryNum(row.Leg, row.Name + ", patilla", 0, errors, out v)) fam.LegMm = v;
                if (row.HasCut && TryNum(row.Cut, row.Name + ", baston", 0, errors, out v)) fam.CutLengthMm = v;
            }

            // refuerzos de zapata
            ReadReinfRows(errors);
            target.FootingReinforcements = _reinfStore.Select(r => r.Clone()).ToList();

            // horizontales por tramos
            target.StemHorizontalBackEnabled = _backOn.IsChecked == true;
            target.StemHorizontalFrontEnabled = _frontOn.IsChecked == true;
            target.StemZoneMode = _modeManual.IsChecked == true ? "manual" : "auto";
            ReadZoneRows(errors);
            target.StemHorizontalZones = _zoneStore.Take(_zoneCount).Select(z => z.Clone()).ToList();
        }

        private bool Collect()
        {
            var errors = new List<string>();
            ReadUi(_cfg, errors);
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
