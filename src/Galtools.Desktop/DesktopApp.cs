using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using IOPath = System.IO.Path;

namespace Galtools
{
    internal sealed class ResourceItem : INotifyPropertyChanged
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string Prefix { get; set; }
        public string Type { get; set; }
        public bool Directory { get; set; }
        public long Size { get; set; }
        public ArchiveEntry Entry { get; set; }
        public string SizeText { get { return Directory ? "文件夹" : DesktopApp.SizeText(Size); } }
        private BitmapSource thumbnail;
        public BitmapSource Thumbnail { get { return thumbnail; } set { thumbnail = value; if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("Thumbnail")); } }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    internal sealed class BrowserLocation
    {
        public string Path, Prefix, Scheme;
    }

    internal sealed class GameView
    {
        public GameRecord Record { get; set; }
        public string Title { get { return Record.Title; } }
        public string Folder { get { return Record.Folder; } }
        public string Tag { get { return string.IsNullOrWhiteSpace(Record.Tags) ? "本地收藏" : Record.Tags.Split(new[] { ',', '，' })[0].Trim(); } }
        public Func<GameRecord, BitmapSource> CoverLoader { get; set; }
        public BitmapSource Cover { get { return CoverLoader == null ? null : CoverLoader(Record); } }
    }

    internal sealed partial class DesktopApp
    {
        private readonly Window window;
        private readonly List<GameRecord> games;
        private readonly List<string> schemes;
        private readonly List<BrowserLocation> history = new List<BrowserLocation>();
        private int historyIndex = -1;
        private IArchiveBackend archive;
        private string folder, archivePath, prefix = "";
        private List<ResourceItem> allItems = new List<ResourceItem>();
        private List<ResourceItem> visibleItems = new List<ResourceItem>();
        private List<GameView> gameViews = new List<GameView>();
        private readonly Dictionary<string, BitmapSource> coverCache = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
        private readonly List<List<Border>> cards = new List<List<Border>>();
        private double wallDistance;
        private readonly List<Canvas> lanes = new List<Canvas>();
        private int wallColumns;
        private double wallCardHeight, wallStep;
        private bool libraryMode;
        private readonly Stopwatch animationClock = Stopwatch.StartNew();
        private double previousFrame;
        private bool manualPause, resources, settings, rebuilding, gridMode, closing, previewBusy, thumbnailBusy;
        private int previewVersion, navigationVersion, tilePage;
        private volatile bool exportCancelled;
        private SoundPlayer player;
        private MemoryStream audio;
        private readonly DispatcherTimer thumbnailTimer;
        private const int TilePageSize = 60;

        internal Window Window { get { return window; } }
        private T C<T>(string name) where T : class { return window.FindName(name) as T; }
        private static Brush B(string color) { return (Brush)new BrushConverter().ConvertFromString(darkTheme ? DarkColor(color) : color); }
        private static TextBlock Text(string text, double size, string color, bool bold = false)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = B(color), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
        }

        public DesktopApp()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Galtools.Shell.xaml")) window = (Window)XamlReader.Load(stream);
            window.Width = Math.Min(1440, SystemParameters.WorkArea.Width - 32);
            window.Height = Math.Min(900, SystemParameters.WorkArea.Height - 32);
            preferences = UiSettingsStore.Load();
            darkTheme = preferences.DarkTheme;
            ApplyTheme();
            try { games = GameStore.Load(); } catch { games = new List<GameRecord>(); }
            try { schemes = GarbroModClient.ListSchemes(); } catch { schemes = new List<string>(); }
            Wire();
            RefreshSidebarImages();
            RefreshGames();
            string initial = games.Where(g => Directory.Exists(g.Folder)).Select(g => g.Folder).FirstOrDefault() ?? AppDomain.CurrentDomain.BaseDirectory;
            NavigateFolder(Directory.Exists(initial) ? initial : AppDomain.CurrentDomain.BaseDirectory, true);
            CompositionTarget.Rendering += RenderWall;
            thumbnailTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
            thumbnailTimer.Tick += delegate { LoadOneThumbnail(); }; thumbnailTimer.Start();
            StartSidebarTimer();
            window.Closed += delegate {
                StopUnpackNotice();
                closing = true; ++previewVersion; ++navigationVersion; exportCancelled = true;
                thumbnailTimer.Stop(); if (sidebarTimer != null) sidebarTimer.Stop(); CompositionTarget.Rendering -= RenderWall; StopAudio();
                var old = archive; archive = null; if (old != null) Task.Run(() => old.Dispose());
            };
            window.PreviewKeyDown += delegate(object sender, KeyEventArgs e) {
                if (e.Key == Key.Escape && C<Border>("ModalOverlay").Visibility == Visibility.Visible) { CloseModal(); e.Handled = true; }
            };
        }

        private void Wire()
        {
            SetButton("NavGames", "game", "游戏管理", () => ShowPage(false));
            SetButton("NavResources", "folder", "资源浏览", () => ShowPage(true));
            SetButton("NavSettings", "settings", "设置", ShowSettings);
            SetButton("AddGame", "plus", "添加游戏", () => EditGame(null));
            C<Button>("EmptyAdd").Click += delegate { EditGame(null); };
            C<Button>("ModeFlow").Click += delegate { SetLibraryMode(false); };
            C<Button>("ModeLibrary").Click += delegate { SetLibraryMode(true); };
            SetButton("PauseWall", "pause", "暂停流动", delegate { manualPause = !manualPause; ButtonContent(C<Button>("PauseWall"), manualPause ? "play" : "pause", manualPause ? "继续流动" : "暂停流动"); });
            C<TextBox>("GameSearch").TextChanged += delegate { if (!rebuilding) RefreshGames(); };
            C<ComboBox>("GameCategory").SelectionChanged += delegate { if (!rebuilding) RefreshGames(); };
            C<ComboBox>("GameSort").ItemsSource = new[] { "按添加顺序", "按名称排序" }; C<ComboBox>("GameSort").SelectedIndex = 0;
            C<ComboBox>("GameSort").SelectionChanged += delegate { if (!rebuilding) RefreshGames(); };
            SetButton("Back", "left", "", () => GoHistory(-1)); SetButton("Forward", "right", "", () => GoHistory(1)); SetButton("Up", "up", "", GoUp);
            SetButton("OpenFolder", "folder", "打开文件夹", delegate { string path = PickFolder(); if (path != null) NavigateFolder(path, true); });
            var scheme = C<ComboBox>("Scheme"); scheme.Items.Add("自动"); foreach (var s in schemes) scheme.Items.Add(s); scheme.Text = "自动";
            C<Button>("Reopen").Click += delegate { if (archivePath != null) OpenArchive(archivePath, false, scheme.Text); };
            C<TextBox>("Address").KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key != Key.Enter) return; string p = C<TextBox>("Address").Text.Trim(); if (Directory.Exists(p)) NavigateFolder(p, true); else if (File.Exists(p)) OpenArchive(p, true, scheme.Text); else Error("路径不存在。请输入文件夹或封包的实际路径。"); e.Handled = true; };
            C<ComboBox>("FileType").ItemsSource = new[] { "全部文件", "图片", "音频", "封包", "其他" }; C<ComboBox>("FileType").SelectedIndex = 0;
            C<ComboBox>("FileSort").ItemsSource = new[] { "按名称排序", "按大小排序" }; C<ComboBox>("FileSort").SelectedIndex = 0;
            C<TextBox>("FileSearch").TextChanged += delegate { ApplyFileFilter(); };
            C<ComboBox>("FileType").SelectionChanged += delegate { ApplyFileFilter(); };
            C<ComboBox>("FileSort").SelectionChanged += delegate { ApplyFileFilter(); };
            C<DataGrid>("Files").SelectionChanged += delegate { if (!rebuilding && !gridMode) StartPreview(); };
            C<DataGrid>("Files").MouseDoubleClick += delegate { OpenSelected(); };
            C<ListBox>("Tiles").SelectionChanged += delegate { if (!rebuilding && gridMode) StartPreview(); };
            C<ListBox>("Tiles").MouseDoubleClick += delegate { OpenSelected(); };
            C<DataGrid>("Files").PreviewKeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { OpenSelected(); e.Handled = true; } };
            C<ListBox>("Tiles").PreviewKeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { OpenSelected(); e.Handled = true; } };
            SetButton("ListMode", "list", "", () => SetView(false)); SetButton("GridMode", "grid", "", () => SetView(true));
            SetButton("SaveRaw", "file", "保存原文件", () => SaveSelected("R", null));
            SetButton("SavePng", "image", "转换 PNG", () => SaveSelected("I", ".png"));
            SetButton("SaveWav", "music", "转换 WAV", () => SaveSelected("A", ".wav"));
            SetButton("SaveMp3", "music", "转换 MP3", () => SaveSelected("M", ".mp3"));
            C<Button>("SavePng").Style = (Style)window.FindResource("SoftButton"); ButtonContent(C<Button>("SavePng"), "image", "转换 PNG");
            C<Button>("SaveWav").Style = (Style)window.FindResource("PinkButton"); ButtonContent(C<Button>("SaveWav"), "music", "转换 WAV");
            C<Button>("SaveMp3").Background = B("#F5F1FE"); C<Button>("SaveMp3").Foreground = B("#8470BE"); C<Button>("SaveMp3").BorderBrush = B("#E9E0FA"); ButtonContent(C<Button>("SaveMp3"), "music", "转换 MP3");
            SetButton("ExportAll", "download", "导出全部", ExportAll);
            C<Button>("CancelExport").Click += delegate { exportCancelled = true; };
            C<Button>("PlayAudio").Click += delegate { try { if (player != null) player.Play(); } catch (Exception ex) { Error(ex.Message); } };
            SetButton("ExpandPreview", "expand", "", ExpandPreview);
            SetButton("CloseModal", "close", "", CloseModal);
            C<Grid>("WallViewport").SizeChanged += delegate { LayoutWall(); LayoutLibrary(); };
            WireSettings();
            C<TreeView>("Directories").SelectedItemChanged += delegate {
                if (rebuilding) return;
                var node = C<TreeView>("Directories").SelectedItem as TreeViewItem;
                if (node == null) return; var location = node.Tag as BrowserLocation; if (location == null) return;
                if (Directory.Exists(location.Path)) { if (location.Path != folder || archive != null) NavigateFolder(location.Path, true); }
                else if (location.Path == archivePath && location.Prefix != prefix) { prefix = location.Prefix ?? ""; RebuildArchiveItems(); Record(new BrowserLocation { Path = archivePath, Prefix = prefix, Scheme = scheme.Text }); }
            };
            SetView(false);
            // Tile pages bound UI work and bitmap memory even for archives with tens of thousands of entries.
            C<Button>("PreviousTiles").Click += delegate { if (tilePage > 0) { tilePage--; FillTiles(null); } };
            C<Button>("NextTiles").Click += delegate { if ((tilePage + 1) * TilePageSize < visibleItems.Count) { tilePage++; FillTiles(null); } };
        }

        private void SetButton(string name, string icon, string label, Action action)
        {
            var button = C<Button>(name); ButtonContent(button, icon, label); button.Click += delegate { action(); };
        }
        private static void ButtonContent(Button button, string icon, string label)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var path = Icon(icon, 17, null); path.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground") { Source = button });
            panel.Children.Add(path);
            if (label.Length > 0) {
                var caption = new TextBlock { Text = label, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                caption.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { Source = button });
                panel.Children.Add(caption);
            }
            button.Content = panel;
            System.Windows.Automation.AutomationProperties.SetName(button, label.Length > 0 ? label : icon);
        }
        private static System.Windows.Shapes.Path Icon(string name, double size, Brush color)
        {
            string d;
            switch (name) {
                case "game": d = "M4,7 C1,6 0,9 0,14 C0,18 2,19 5,15 L15,15 C18,19 20,18 20,14 C20,9 19,6 16,7 Z M4,11 L8,11 M6,9 L6,13 M14,10 L14,10.5 M16,12 L16,12.5"; break;
                case "folder": d = "M1,5 L8,5 L10,7 L20,7 L20,18 L1,18 Z M1,7 L1,3 L8,3 L10,5 L19,5"; break;
                case "plus": d = "M10,2 L10,18 M2,10 L18,10"; break;
                case "left": d = "M13,3 L6,10 L13,17"; break;
                case "right": d = "M7,3 L14,10 L7,17"; break;
                case "up": d = "M3,9 L10,2 L17,9 M10,2 L10,19"; break;
                case "close": d = "M3,3 L17,17 M17,3 L3,17"; break;
                case "pause": d = "M6,3 L6,17 M14,3 L14,17"; break;
                case "play": d = "M5,2 L18,10 L5,18 Z"; break;
                case "list": d = "M7,4 L19,4 M7,10 L19,10 M7,16 L19,16 M1,4 L2,4 M1,10 L2,10 M1,16 L2,16"; break;
                case "grid": d = "M1,1 L8,1 L8,8 L1,8 Z M12,1 L19,1 L19,8 L12,8 Z M1,12 L8,12 L8,19 L1,19 Z M12,12 L19,12 L19,19 L12,19 Z"; break;
                case "image": d = "M1,2 L19,2 L19,18 L1,18 Z M2,15 L7,9 L12,14 L15,10 L19,15 M13,6 L14,6"; break;
                case "music": d = "M8,15 L8,3 L19,1 L19,13 M8,6 L19,4 M8,15 C8,19 1,19 1,16 C1,13 8,12 8,15 M19,13 C19,17 12,17 12,14 C12,11 19,10 19,13"; break;
                case "download": d = "M10,1 L10,13 M5,8 L10,13 L15,8 M2,13 L2,19 L18,19 L18,13"; break;
                case "expand": d = "M2,8 L2,2 L8,2 M12,2 L18,2 L18,8 M18,12 L18,18 L12,18 M8,18 L2,18 L2,12"; break;
                case "edit": d = "M3,14 L2,19 L7,18 L18,7 L13,2 Z M11,4 L16,9"; break;
                case "copy": d = "M7,6 L18,6 L18,19 L7,19 Z M13,6 L13,1 L2,1 L2,14 L7,14"; break;
                case "settings": d = "M10,2 L12,2 L13,5 L15,6 L18,5 L19,8 L17,10 L17,12 L19,14 L18,17 L15,16 L13,17 L12,20 L8,20 L7,17 L5,16 L2,17 L1,14 L3,12 L3,10 L1,8 L2,5 L5,6 L7,5 L8,2 Z M10,7 A3,3 0 1 0 10,13 A3,3 0 1 0 10,7"; break;
                default: d = "M3,1 L12,1 L18,7 L18,19 L3,19 Z M12,1 L12,7 L18,7 M6,11 L14,11 M6,15 L12,15"; break;
            }
            return new System.Windows.Shapes.Path { Data = Geometry.Parse(d), Width = size, Height = size, Stretch = Stretch.Uniform, Stroke = color ?? B("#468CFA"), StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, VerticalAlignment = VerticalAlignment.Center };
        }

        private void ShowPage(bool showResources)
        {
            settings = false; resources = showResources;
            C<Grid>("GamesPage").Visibility = resources ? Visibility.Collapsed : Visibility.Visible;
            C<Grid>("ResourcesPage").Visibility = resources ? Visibility.Visible : Visibility.Collapsed;
            C<Grid>("SettingsPage").Visibility = Visibility.Collapsed;
            UpdateNavigation();
        }

        private BitmapSource Cover(GameRecord game)
        {
            string path = game.CoverPath;
            try {
                if (string.IsNullOrWhiteSpace(path) && Directory.Exists(game.Folder))
                    path = Directory.GetFiles(game.Folder).Where(p => new[] { ".png", ".jpg", ".jpeg", ".bmp" }.Contains(IOPath.GetExtension(p).ToLowerInvariant()))
                        .OrderByDescending(p => IOPath.GetFileName(p).IndexOf("cover", StringComparison.OrdinalIgnoreCase) >= 0).FirstOrDefault();
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
                string key = path + File.GetLastWriteTimeUtc(path).Ticks;
                BitmapSource image; if (coverCache.TryGetValue(key, out image)) return image;
                image = LoadBitmap(File.ReadAllBytes(path), 1200); if (coverCache.Count >= 48) coverCache.Remove(coverCache.Keys.First()); coverCache[key] = image; return image;
            } catch { return null; }
        }

        private void RefreshGames()
        {
            rebuilding = true;
            string category = C<ComboBox>("GameCategory").SelectedItem as string ?? "全部游戏";
            var categories = new[] { "全部游戏" }.Concat(games.SelectMany(g => (g.Tags ?? "").Split(new[] { ',', '，' })).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct()).ToList();
            C<ComboBox>("GameCategory").ItemsSource = categories; C<ComboBox>("GameCategory").SelectedItem = categories.Contains(category) ? category : "全部游戏";
            string query = C<TextBox>("GameSearch").Text.Trim();
            IEnumerable<GameRecord> view = games.Where(g => query.Length == 0 || ((g.Title ?? "") + " " + g.Folder + " " + g.Tags).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
            if (category != "全部游戏") view = view.Where(g => (g.Tags ?? "").Split(new[] { ',', '，' }).Any(s => s.Trim() == category));
            if (C<ComboBox>("GameSort").SelectedIndex == 1) view = view.OrderBy(g => g.Title);
            gameViews = view.Select(g => new GameView { Record = g, CoverLoader = Cover }).ToList();
            wallDistance = 0;
            C<TextBlock>("GameCount").Text = games.Count + " 部收藏";
            C<Border>("EmptyLibrary").Visibility = gameViews.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var lane in lanes) lane.Children.Clear();
            foreach (var laneCards in cards) laneCards.Clear();
            rebuilding = false; LayoutWall(); LayoutLibrary();
        }

        private Border CreateGameCard(double height, double imageHeight, bool compact)
        {
            var card = new Border { Height = height, CornerRadius = new CornerRadius(15), Background = (Brush)window.FindResource("CardSurface"), BorderBrush = B("#E2E9F5"), BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Focusable = true };
            card.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Color.FromRgb(97, 129, 180), BlurRadius = 12, ShadowDepth = 3, Opacity = .09 };
            var content = new Grid(); content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(imageHeight) }); content.RowDefinitions.Add(new RowDefinition());
            var cover = new Border { Background = (Brush)window.FindResource("SoftFill"), CornerRadius = new CornerRadius(14, 14, 0, 0), ClipToBounds = true };
            var coverGrid = new Grid(); var coverLabel = Text("GALTOOLS  /  COLLECTION", 12, "#94A6C7"); coverLabel.HorizontalAlignment = HorizontalAlignment.Center; coverLabel.VerticalAlignment = VerticalAlignment.Center;
            coverGrid.Children.Add(coverLabel);
            var image = new Image { Stretch = Stretch.UniformToFill }; image.SetBinding(Image.SourceProperty, "Cover"); RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality); coverGrid.Children.Add(image); cover.Child = coverGrid;
            cover.SizeChanged += delegate { cover.Clip = new RectangleGeometry(new Rect(0,0,cover.ActualWidth,cover.ActualHeight), 14, 14); };
            content.Children.Add(cover);
            var footer = new Grid { Margin = new Thickness(compact ? 10 : 15, compact ? 7 : 10, compact ? 10 : 15, compact ? 8 : 11) }; Grid.SetRow(footer, 1);
            footer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(compact ? 22 : 25) }); footer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(compact ? 0 : 22) }); footer.RowDefinitions.Add(new RowDefinition());
            var title = new TextBlock { FontSize = compact ? 13 : 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis }; title.SetBinding(TextBlock.TextProperty, "Title"); footer.Children.Add(title);
            var folderText = new TextBlock { FontSize = 11, Foreground = B("#7B8AA3"), TextTrimming = TextTrimming.CharacterEllipsis, Visibility = compact ? Visibility.Collapsed : Visibility.Visible }; folderText.SetBinding(TextBlock.TextProperty, "Folder"); Grid.SetRow(folderText, 1); footer.Children.Add(folderText);
            var tag = new Border { Background = B("#FCEBF3"), CornerRadius = new CornerRadius(8), Padding = new Thickness(compact ? 5 : 9, 4, compact ? 5 : 9, 4), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, MaxWidth = compact ? 75 : 115 };
            var tagText = new TextBlock { FontSize = 10, Foreground = B("#CE6D99"), TextTrimming = TextTrimming.CharacterEllipsis }; tagText.SetBinding(TextBlock.TextProperty, "Tag"); tag.Child = tagText; Grid.SetRow(tag, 2); footer.Children.Add(tag);
            var play = new Button { Style = (Style)window.FindResource("PrimaryButton"), Padding = new Thickness(compact ? 6 : 10, 5, compact ? 6 : 10, 5), MinHeight = 29, HorizontalAlignment = HorizontalAlignment.Right, FontSize = 11 };
            ButtonContent(play, "play", compact ? "" : "启动游戏"); play.ToolTip = "启动游戏"; Grid.SetRow(play, 2); footer.Children.Add(play);
            play.Click += delegate(object sender, RoutedEventArgs e) { var g = card.DataContext as GameView; if (g != null) LaunchGame(g.Record); e.Handled = true; };
            content.Children.Add(footer); card.Child = content;
            card.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e) { if (e.OriginalSource is Button) return; var g = card.DataContext as GameView; if (g != null) ShowGame(g.Record); e.Handled = true; };
            card.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { var g = card.DataContext as GameView; if (g != null) ShowGame(g.Record); e.Handled = true; } };
            card.MouseEnter += delegate { card.BorderBrush = B("#A9CAFF"); }; card.MouseLeave += delegate { card.BorderBrush = B("#E2E9F5"); };
            return card;
        }

        private int ColumnCount(double width)
        {
            int n = gameViews.Count;
            int target = n <= 10 ? 3 : n <= 18 ? 4 : n <= 30 ? 5 : n <= 50 ? 6 : n <= 80 ? 7 : 8;
            int fit = Math.Max(1, (int)Math.Floor(Math.Max(300, width) / 178));
            return Math.Min(target, fit);
        }
        private void LayoutWall()
        {
            if (gameViews.Count == 0 || libraryMode) return;
            var viewport = C<Grid>("WallViewport");
            if (viewport.ActualWidth < 200 || viewport.ActualHeight < 100) return;
            int count = ColumnCount(viewport.ActualWidth);
            double width = Math.Max(150, viewport.ActualWidth / count - 14);
            double height = Math.Max(192, Math.Min(272, width * .67 + 80));
            double step = height + 15;
            bool changed = count != wallColumns || Math.Abs(height - wallCardHeight) > 3;
            if (changed) {
                var host = C<Grid>("FlowColumns"); host.Children.Clear(); host.ColumnDefinitions.Clear();
                cards.Clear(); lanes.Clear();
                wallColumns = count; wallCardHeight = height; wallStep = step;
                for (int lane = 0; lane < count; lane++) {
                    host.ColumnDefinitions.Add(new ColumnDefinition());
                    var canvas = new Canvas { ClipToBounds = true, Margin = new Thickness(7,0,7,0) };
                    Grid.SetColumn(canvas, lane); host.Children.Add(canvas); lanes.Add(canvas);
                    cards.Add(new List<Border>());
                }
            }
            int needed = (int)Math.Ceiling((viewport.ActualHeight + wallCardHeight) / wallStep) + 2;
            for (int lane = 0; lane < wallColumns; lane++) {
                var canvas = lanes[lane]; var list = cards[lane];
                while (list.Count < needed) { var card = CreateGameCard(wallCardHeight, wallCardHeight * .59, wallColumns >= 4); list.Add(card); canvas.Children.Add(card); }
                while (list.Count > needed) { canvas.Children.Remove(list[list.Count - 1]); list.RemoveAt(list.Count - 1); }
                foreach (var card in list) card.Width = width;
            }
            PositionWall();
        }
        private static int Mod(int n, int m) { return (n % m + m) % m; }
        private void PositionWall()
        {
            if (gameViews.Count == 0 || libraryMode || wallColumns == 0) return;
            var source = PresentationSource.FromVisual(window); double dpi = source == null ? 1 : source.CompositionTarget.TransformToDevice.M22;
            double viewportHeight = C<Grid>("WallViewport").ActualHeight;
            // A single conveyor runs up column 0, down column 1, and so on.
            // A segment includes the card height: it fully exits before entering
            // the next column. Offscreen queue entries complete the cyclic order.
            double travel = viewportHeight + wallCardHeight;
            for (int lane = 0; lane < wallColumns; lane++) {
                double start = lane * travel;
                int first = (int)Math.Floor((start - wallDistance) / wallStep);
                for (int slot = 0; slot < cards[lane].Count; slot++) {
                    int row = first + slot;
                    double progress = row * wallStep + wallDistance - start;
                    var card = cards[lane][slot];
                    bool visible = progress >= 0 && progress < travel;
                    card.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
                    if (!visible) continue;
                    int index = Mod(-row, gameViews.Count);
                    if (!object.ReferenceEquals(card.DataContext, gameViews[index])) card.DataContext = gameViews[index];
                    double top = lane % 2 == 0 ? viewportHeight - progress : progress - wallCardHeight;
                    Canvas.SetLeft(card, 0); Canvas.SetTop(card, Math.Round(top * dpi) / dpi);
                }
            }
        }
        private void RenderWall(object sender, EventArgs e)
        {
            double now = animationClock.Elapsed.TotalSeconds; double dt = Math.Min(.08, now - previousFrame); previousFrame = now;
            if (resources || settings || libraryMode || manualPause || !window.IsActive || C<Border>("ModalOverlay").Visibility == Visibility.Visible || gameViews.Count == 0) return;
            if (wallStep <= 0) return;
            wallDistance = (wallDistance + dt * (25 + Math.Min(20, gameViews.Count))) % (gameViews.Count * wallStep);
            PositionWall();
        }

        private void ShowModal(string title)
        {
            StopUnpackNotice();
            C<TextBlock>("ModalTitle").Text = title; C<StackPanel>("ModalBody").Children.Clear();
            C<Border>("ModalCard").MaxHeight = Math.Max(400, (window.ActualHeight > 0 ? window.ActualHeight : window.Height) - 90);
            C<Border>("ModalOverlay").Visibility = Visibility.Visible;
            C<Grid>("GamesPage").IsEnabled = false; C<Grid>("ResourcesPage").IsEnabled = false; C<Grid>("SettingsPage").IsEnabled = false;
            C<Button>("NavGames").IsEnabled = false; C<Button>("NavResources").IsEnabled = false; C<Button>("NavSettings").IsEnabled = false;
            C<Button>("CloseModal").Focus();
        }
        private void CloseModal()
        {
            StopUnpackNotice();
            C<Border>("ModalOverlay").Visibility = Visibility.Collapsed; C<StackPanel>("ModalBody").Children.Clear();
            C<Grid>("GamesPage").IsEnabled = true; C<Grid>("ResourcesPage").IsEnabled = true; C<Grid>("SettingsPage").IsEnabled = true;
            C<Button>("NavGames").IsEnabled = true; C<Button>("NavResources").IsEnabled = true; C<Button>("NavSettings").IsEnabled = true;
        }
        private Button ActionButton(string label, string icon, string style, Action action)
        {
            var button = new Button { Margin = new Thickness(0, 0, 8, 0), Style = (Style)window.FindResource(style) };
            ButtonContent(button, icon, label); button.Click += delegate { action(); }; return button;
        }
        private void ShowGame(GameRecord game)
        {
            ShowModal("游戏详情"); var body = C<StackPanel>("ModalBody");
            var art = Cover(game);
            var hero = new Border { Height = 205, CornerRadius = new CornerRadius(12), Background = (Brush)window.FindResource("SoftFill"), Margin = new Thickness(0,0,0,18) };
            if (art != null) hero.Background = new ImageBrush(art) { Stretch = Stretch.UniformToFill };
            body.Children.Add(hero);
            body.Children.Add(Text(game.Title, 23, "#1D2C49", true));
            body.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(game.Tags) ? "本地收藏" : game.Tags.Replace(",", "  ·  "), Foreground = B("#D078A0"), FontSize = 12, Margin = new Thickness(0,7,0,18) });
            var info = new StackPanel();
            AddInfo(info, "游戏目录", game.Folder); AddInfo(info, "启动程序", game.Executable); AddInfo(info, "解密方案", string.IsNullOrWhiteSpace(game.Scheme) ? "自动识别" : game.Scheme); AddInfo(info, "备注", string.IsNullOrWhiteSpace(game.Notes) ? "暂无备注" : game.Notes);
            body.Children.Add(new Border { Background = B("#F7F9FD"), CornerRadius = new CornerRadius(12), Padding = new Thickness(16,8,16,8), Child = info });
            var actions = new WrapPanel { Margin = new Thickness(0,20,0,0) };
            actions.Children.Add(ActionButton("启动游戏", "play", "PrimaryButton", () => LaunchGame(game)));
            actions.Children.Add(ActionButton("浏览资源", "folder", "SoftButton", delegate { CloseModal(); ShowPage(true); C<ComboBox>("Scheme").Text = string.IsNullOrWhiteSpace(game.Scheme) ? "自动" : game.Scheme; NavigateFolder(game.Folder, true); }));
            actions.Children.Add(ActionButton("编辑", "edit", "SoftButton", () => EditGame(game)));
            actions.Children.Add(ActionButton("打开目录", "folder", "SoftButton", delegate { try { if (Directory.Exists(game.Folder)) Process.Start(game.Folder); else Error("游戏目录不存在。"); } catch (Exception ex) { Error(ex.Message); } }));
            actions.Children.Add(ActionButton("移除", "close", "PinkButton", delegate {
                if (MessageBox.Show(window, "从游戏库移除「" + game.Title + "」？游戏文件会保留。", "移除游戏", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                games.Remove(game); SaveGames(); CloseModal(); RefreshGames();
            })); body.Children.Add(actions);
            AddUnpackPromptAction(body, game);
        }
        private static void AddInfo(Panel panel, string label, string value)
        {
            var row = new Grid { Margin = new Thickness(0,8,0,8) }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(83) }); row.ColumnDefinitions.Add(new ColumnDefinition());
            var key = Text(label, 13, "#7B89A2"); var text = Text(value ?? "未设置", 13, "#475978"); Grid.SetColumn(text, 1); row.Children.Add(key); row.Children.Add(text); panel.Children.Add(row);
        }
        private void EditGame(GameRecord original)
        {
            ShowModal(original == null ? "添加游戏" : "编辑游戏"); var body = C<StackPanel>("ModalBody");
            var title = EditField(body, "游戏名称", original == null ? "" : original.Title, "给这段故事起个名字", null);
            TextBox dir = null, exe = null, cover = null;
            exe = EditField(body, "启动程序", original == null ? "" : original.Executable, "绑定游戏的 EXE", delegate { var d = new OpenFileDialog { Filter = "启动程序|*.exe|全部文件|*.*" }; if (d.ShowDialog(window) == true) { exe.Text = d.FileName; dir.Text = IOPath.GetDirectoryName(d.FileName); if (title.Text.Length == 0) title.Text = IOPath.GetFileNameWithoutExtension(d.FileName); } });
            dir = EditField(body, "游戏目录", original == null ? "" : original.Folder, "选择启动程序后自动填写，也可手动修改", delegate { var p = PickFolder(); if (p != null) dir.Text = p; });
            cover = EditField(body, "封面图片", original == null ? "" : original.CoverPath, "留空时尝试使用游戏目录中的图片", delegate { var d = new OpenFileDialog { Filter = "封面图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif" }; if (d.ShowDialog(window) == true) cover.Text = d.FileName; });
            body.Children.Add(new TextBlock { Text = "解密方案", FontSize = 12, Foreground = B("#7D8BA5"), Margin = new Thickness(0,3,0,7) });
            var scheme = new ComboBox { IsEditable = true, ItemsSource = new[] { "自动" }.Concat(schemes).ToList(), Text = original == null || string.IsNullOrWhiteSpace(original.Scheme) ? "自动" : original.Scheme, Margin = new Thickness(0,0,0,12) }; body.Children.Add(scheme);
            var tags = EditField(body, "标签", original == null ? "" : original.Tags, "用逗号分隔，例如：恋爱，校园，已通关", null);
            var notes = EditField(body, "备注", original == null ? "" : original.Notes, "记录你想留下的话…", null); notes.AcceptsReturn = true; notes.Height = 74; notes.TextWrapping = TextWrapping.Wrap;
            var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,8,0,0) };
            buttons.Children.Add(ActionButton("取消", "close", "SoftButton", CloseModal));
            buttons.Children.Add(ActionButton("保存游戏", "plus", "PrimaryButton", delegate {
                if (string.IsNullOrWhiteSpace(title.Text)) { Error("请输入游戏名称。"); return; }
                if (exe.Text.Length > 0 && !File.Exists(exe.Text)) { Error("启动程序不存在。"); return; }
                if (cover.Text.Length > 0 && !File.Exists(cover.Text)) { Error("封面图片不存在。"); return; }
                var game = original ?? new GameRecord(); game.Title = title.Text.Trim(); game.Folder = dir.Text.Trim(); game.Executable = exe.Text.Trim(); game.CoverPath = cover.Text.Trim(); game.Tags = tags.Text.Trim(); game.Notes = notes.Text.Trim(); game.Scheme = scheme.Text == "自动" ? "" : scheme.Text.Trim();
                if (original == null) games.Add(game); SaveGames(); CloseModal(); RefreshGames();
            })); body.Children.Add(buttons); title.Focus();
        }
        private TextBox EditField(Panel parent, string label, string value, string hint, Action browse)
        {
            parent.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = B("#7D8BA5"), Margin = new Thickness(0,3,0,7) });
            var row = new DockPanel { Margin = new Thickness(0,0,0,12) };
            if (browse != null) { var button = ActionButton("选择", "folder", "SoftButton", browse); button.Margin = new Thickness(8,0,0,0); DockPanel.SetDock(button, Dock.Right); row.Children.Add(button); }
            var input = new TextBox { Text = value ?? "", Tag = hint }; row.Children.Add(input); parent.Children.Add(row); return input;
        }
        private void SaveGames() { try { GameStore.Save(games); } catch (Exception ex) { Error(ex.Message); } }
        private void LaunchGame(GameRecord game)
        {
            if (!File.Exists(game.Executable)) { Error("请先编辑游戏并绑定有效的启动程序。"); return; }
            try { Process.Start(new ProcessStartInfo(game.Executable) { WorkingDirectory = IOPath.GetDirectoryName(game.Executable) }); } catch (Exception ex) { Error(ex.Message); }
        }

        private string PickFolder()
        {
            using (var d = new System.Windows.Forms.FolderBrowserDialog()) { if (Directory.Exists(folder)) d.SelectedPath = folder; return d.ShowDialog(new WindowOwner(window)) == System.Windows.Forms.DialogResult.OK ? d.SelectedPath : null; }
        }
        private sealed class WindowOwner : System.Windows.Forms.IWin32Window
        {
            public IntPtr Handle { get; private set; }
            public WindowOwner(Window window) { Handle = new System.Windows.Interop.WindowInteropHelper(window).Handle; }
        }
        private void NavigateFolder(string path, bool record)
        {
            try {
                path = IOPath.GetFullPath(path); if (!Directory.Exists(path)) throw new DirectoryNotFoundException("游戏目录不存在：" + path);
                var items = Directory.GetDirectories(path).OrderBy(p => p).Select(p => new ResourceItem { Name = IOPath.GetFileName(p), Path = p, Directory = true, Type = "文件夹" }).ToList();
                items.AddRange(Directory.GetFiles(path).OrderBy(p => p).Select(p => new ResourceItem { Name = IOPath.GetFileName(p), Path = p, Type = FileType(p), Size = new FileInfo(p).Length }));
                ++navigationVersion; ++previewVersion; exportCancelled = true;
                var old = archive; archive = null; if (old != null) Task.Run(() => old.Dispose());
                archivePath = null; prefix = ""; folder = path; allItems = items;
                RebuildBrowser(); if (record) Record(new BrowserLocation { Path = path, Scheme = C<ComboBox>("Scheme").Text });
            } catch (Exception ex) { Error(ex.Message); }
        }
        private string EffectiveScheme(string path, string selected)
        {
            if (!string.IsNullOrWhiteSpace(selected) && selected != "自动") return selected;
            foreach (var game in games) {
                try { if (!string.IsNullOrWhiteSpace(game.Folder) && !string.IsNullOrWhiteSpace(game.Scheme) && path.StartsWith(IOPath.GetFullPath(game.Folder).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return game.Scheme; } catch { }
            }
            try { foreach (var exe in Directory.GetFiles(IOPath.GetDirectoryName(path), "*.exe")) { string name = IOPath.GetFileNameWithoutExtension(exe); var match = schemes.FirstOrDefault(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase)); if (match != null) return match; } } catch { }
            return "";
        }
        private async void OpenArchive(string path, bool record, string selected, string targetPrefix = "")
        {
            int version = ++navigationVersion; SetStatus("正在打开封包…");
            IArchiveBackend next = null;
            try {
                path = IOPath.GetFullPath(path); string scheme = EffectiveScheme(path, selected);
                next = await Task.Run(() => BackendRegistry.Open(path, scheme));
                if (closing || version != navigationVersion) { next.Dispose(); return; }
                var old = archive; archive = next; next = null; if (old != null) DisposeBackendLater(old);
                ++previewVersion; exportCancelled = true; archivePath = path; folder = IOPath.GetDirectoryName(path); prefix = targetPrefix ?? "";
                RebuildArchiveItems(); if (record) Record(new BrowserLocation { Path = path, Prefix = prefix, Scheme = selected });
            } catch (Exception ex) { if (!closing && version == navigationVersion) Error(ex.Message); }
            finally { if (next != null) next.Dispose(); }
        }
        private void RebuildArchiveItems()
        {
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var items = new List<ResourceItem>();
            foreach (var entry in archive.Entries) {
                string name = entry.Name.Replace('\\', '/'); if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string rest = name.Substring(prefix.Length); int slash = rest.IndexOf('/');
                if (slash >= 0) { string dir = rest.Substring(0, slash); if (dir.Length > 0 && dirs.Add(dir)) items.Add(new ResourceItem { Name = dir, Prefix = prefix + dir + "/", Directory = true, Type = "文件夹" }); }
                else if (rest.Length > 0) items.Add(new ResourceItem { Name = rest, Entry = entry, Size = entry.Size, Type = TypeName(entry.Type), Path = archivePath + " / " + entry.Name });
            }
            allItems = items.OrderByDescending(i => i.Directory).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList(); RebuildBrowser();
        }
        private void RebuildBrowser()
        {
            failedThumbnails.Clear();
            C<TextBox>("Address").Text = archivePath ?? folder;
            C<TextBlock>("LocationName").Text = archivePath == null ? IOPath.GetFileName(folder) : prefix.Length == 0 ? IOPath.GetFileName(archivePath) : prefix.TrimEnd('/').Split('/').Last();
            rebuilding = true;
            var tree = C<TreeView>("Directories"); tree.Items.Clear();
            if (archivePath != null) {
                var parent = Node("返回游戏目录", new BrowserLocation { Path = folder }); tree.Items.Add(parent);
                var root = Node(IOPath.GetFileName(archivePath), new BrowserLocation { Path = archivePath, Prefix = "" }); tree.Items.Add(root);
                var current = root; string walk = "";
                foreach (var part in prefix.Split(new[] {'/'}, StringSplitOptions.RemoveEmptyEntries)) { walk += part + "/"; var child = Node(part, new BrowserLocation { Path = archivePath, Prefix = walk }); current.Items.Add(child); current = child; }
                foreach (var dir in allItems.Where(i => i.Directory)) current.Items.Add(Node(dir.Name, new BrowserLocation { Path = archivePath, Prefix = dir.Prefix }));
            } else {
                var parent = Directory.GetParent(folder); if (parent != null) tree.Items.Add(Node("上一级目录", new BrowserLocation { Path = parent.FullName }));
                var root = Node(IOPath.GetFileName(folder).Length == 0 ? folder : IOPath.GetFileName(folder), new BrowserLocation { Path = folder }); tree.Items.Add(root);
                foreach (var dir in allItems.Where(i => i.Directory)) root.Items.Add(Node(dir.Name, new BrowserLocation { Path = dir.Path }));
            }
            rebuilding = false; ApplyFileFilter(); UpdateHistory(); SetStatus(archive == null ? "本地文件夹  ·  双击文件夹或封包进入" : archive.Description + "  ·  " + archive.Entries.Count + " 个条目");
        }
        private TreeViewItem Node(string name, BrowserLocation location)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal }; header.Children.Add(Icon("folder", 14, B("#77A4E7"))); header.Children.Add(new TextBlock { Text = name, Margin = new Thickness(7,0,0,0), FontSize = 11, MaxWidth = 115, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = name });
            return new TreeViewItem { Header = header, Tag = location, IsExpanded = true };
        }
        private void ApplyFileFilter()
        {
            if (rebuilding) return;
            string query = C<TextBox>("FileSearch").Text.Trim(); int type = C<ComboBox>("FileType").SelectedIndex;
            IEnumerable<ResourceItem> view = allItems.Where(i => query.Length == 0 || i.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
            if (type > 0) view = view.Where(i => type == 1 ? i.Type == "图像" : type == 2 ? i.Type == "音频" : type == 3 ? i.Type == "封包" : i.Type != "图像" && i.Type != "音频" && i.Type != "封包");
            view = C<ComboBox>("FileSort").SelectedIndex == 1 ? view.OrderByDescending(i => i.Directory).ThenByDescending(i => i.Size) : view.OrderByDescending(i => i.Directory).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase);
            visibleItems = view.ToList(); tilePage = 0;
            rebuilding = true; C<DataGrid>("Files").ItemsSource = visibleItems; C<DataGrid>("Files").SelectedIndex = visibleItems.Count > 0 ? 0 : -1; rebuilding = false;
            FillTiles(null); C<TextBlock>("FileCount").Text = visibleItems.Count + " 个文件"; if (!gridMode) StartPreview();
        }
        private void FillTiles(ResourceItem selected)
        {
            rebuilding = true;
            C<ListBox>("Tiles").ItemsSource = visibleItems.Skip(tilePage * TilePageSize).Take(TilePageSize).ToList();
            C<ListBox>("Tiles").SelectedItem = selected; if (C<ListBox>("Tiles").SelectedIndex < 0 && C<ListBox>("Tiles").Items.Count > 0) C<ListBox>("Tiles").SelectedIndex = 0;
            C<TextBlock>("TilePageLabel").Text = (tilePage + 1) + " / " + Math.Max(1, (visibleItems.Count + TilePageSize - 1) / TilePageSize);
            C<Button>("PreviousTiles").IsEnabled = tilePage > 0; C<Button>("NextTiles").IsEnabled = (tilePage + 1) * TilePageSize < visibleItems.Count;
            rebuilding = false; if (gridMode) StartPreview();
        }
        private void SetView(bool grid)
        {
            var selected = Selected(); gridMode = grid;
            C<DataGrid>("Files").Visibility = grid ? Visibility.Collapsed : Visibility.Visible;
            C<ListBox>("Tiles").Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
            C<StackPanel>("TilePager").Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
            C<Button>("ListMode").Background = B(grid ? "#FFFFFF" : "#EAF3FF"); C<Button>("GridMode").Background = B(grid ? "#EAF3FF" : "#FFFFFF");
            if (grid) { int index = selected == null ? 0 : visibleItems.IndexOf(selected); tilePage = Math.Max(0, index / TilePageSize); FillTiles(selected); }
            else if (selected != null) C<DataGrid>("Files").SelectedItem = selected;
            if (!gridMode) StartPreview();
        }
        private ResourceItem Selected() { return gridMode ? C<ListBox>("Tiles").SelectedItem as ResourceItem : C<DataGrid>("Files").SelectedItem as ResourceItem; }
        private void OpenSelected()
        {
            var item = Selected(); if (item == null) return;
            if (archive == null) { if (item.Directory) NavigateFolder(item.Path, true); else if (item.Type == "图像" || item.Type == "音频") StartPreview(); else OpenArchive(item.Path, true, C<ComboBox>("Scheme").Text); }
            else if (item.Directory) { prefix = item.Prefix; RebuildArchiveItems(); Record(new BrowserLocation { Path = archivePath, Prefix = prefix, Scheme = C<ComboBox>("Scheme").Text }); }
        }
        private void Record(BrowserLocation location)
        {
            if (historyIndex < history.Count - 1) history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1);
            history.Add(location); historyIndex = history.Count - 1; UpdateHistory();
        }
        private void UpdateHistory() { C<Button>("Back").IsEnabled = historyIndex > 0; C<Button>("Forward").IsEnabled = historyIndex >= 0 && historyIndex < history.Count - 1; }
        private void GoHistory(int delta)
        {
            int next = historyIndex + delta; if (next < 0 || next >= history.Count) return;
            historyIndex = next; var loc = history[next]; C<ComboBox>("Scheme").Text = loc.Scheme ?? "自动";
            if (Directory.Exists(loc.Path)) NavigateFolder(loc.Path, false); else OpenArchive(loc.Path, false, loc.Scheme, loc.Prefix); UpdateHistory();
        }
        private void GoUp()
        {
            if (archive != null && prefix.Length > 0) { string cut = prefix.TrimEnd('/'); int last = cut.LastIndexOf('/'); prefix = last < 0 ? "" : cut.Substring(0, last + 1); RebuildArchiveItems(); Record(new BrowserLocation { Path = archivePath, Prefix = prefix, Scheme = C<ComboBox>("Scheme").Text }); }
            else if (archive != null) NavigateFolder(folder, true);
            else if (folder != null) { var parent = Directory.GetParent(folder); if (parent != null) NavigateFolder(parent.FullName, true); }
        }

        internal static string SizeText(long size) { return size >= 1048576 ? (size / 1048576.0).ToString("0.0") + " MB" : size >= 1024 ? (size / 1024.0).ToString("0.0") + " KB" : size + " B"; }
        private static string FileType(string path)
        {
            string ext = IOPath.GetExtension(path).ToLowerInvariant();
            if (new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tlg", ".tga", ".tif", ".tiff" }.Contains(ext)) return "图像";
            if (new[] { ".ogg", ".mp3", ".wav", ".flac", ".opus", ".m4a" }.Contains(ext)) return "音频";
            if (new[] { ".xp3", ".paz", ".pak", ".arc", ".dat", ".pfs", ".pac", ".zip", ".7z" }.Contains(ext)) return "封包";
            return "文件";
        }
        private static string TypeName(string type) { return type == "image" ? "图像" : type == "audio" ? "音频" : type == "archive" ? "封包" : string.IsNullOrEmpty(type) ? "文件" : type; }
        private static BitmapSource LoadBitmap(byte[] bytes, int width = 0)
        {
            using (var stream = new MemoryStream(bytes)) {
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                if (width > 0) image.DecodePixelWidth = width;
                image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
            }
        }
        private void StopAudio() { if (player != null) { player.Stop(); player.Dispose(); player = null; } if (audio != null) { audio.Dispose(); audio = null; } }
        private async void StartPreview()
        {
            int version = ++previewVersion; StopAudio(); var item = Selected(); var client = archive;
            C<Image>("PreviewImage").Source = null; C<Button>("PlayAudio").Visibility = Visibility.Collapsed;
            C<TextBlock>("PreviewName").Text = item == null ? "文件详情" : item.Name;
            C<TextBlock>("PreviewBadge").Text = item == null ? "尚未选择" : item.Type + (item.Directory ? "" : "  ·  " + item.SizeText);
            C<TextBlock>("PreviewInfo").Text = item == null ? "选择文件查看信息" : "类型      " + item.Type + "\n大小      " + item.SizeText + "\n位置      " + (item.Path ?? archivePath);
            C<TextBlock>("PreviewEmpty").Visibility = Visibility.Visible;
            C<TextBlock>("PreviewEmpty").Text = item != null && item.Directory ? "文件夹" : "选择一个文件";
            C<TextBlock>("PreviewStatus").Text = item != null && item.Directory ? "双击文件夹即可进入。" : "";
            bool enabled = item != null && !item.Directory;
            bool possiblyImage = enabled && (client == null || item.Entry.Type == "image" || string.IsNullOrEmpty(item.Entry.Type));
            bool possiblyAudio = enabled && client != null && (item.Entry.Type == "audio" || string.IsNullOrEmpty(item.Entry.Type));
            C<Button>("SaveRaw").IsEnabled = enabled; C<Button>("SavePng").IsEnabled = possiblyImage; C<Button>("SaveWav").IsEnabled = possiblyAudio; C<Button>("SaveMp3").IsEnabled = possiblyAudio; C<Button>("ExportAll").IsEnabled = client != null;
            if (!enabled) { previewBusy = false; return; }
            previewBusy = true; C<TextBlock>("PreviewStatus").Text = "正在加载预览…";
            try {
                byte[] sound = null; BitmapSource image = await Task.Run(() => {
                    if (version != previewVersion) return null;
                    if (client == null) {
                        if (item.Type == "图像") return LoadBitmap(File.ReadAllBytes(item.Path), 960);
                        if (IOPath.GetExtension(item.Path).Equals(".wav", StringComparison.OrdinalIgnoreCase)) { sound = File.ReadAllBytes(item.Path); return null; }
                        throw new InvalidDataException("此文件暂无内置预览，可保存原文件。");
                    }
                    if (item.Entry.Type == "audio") { sound = client.ReadAudioWav(item.Entry.Index); return null; }
                    try { return LoadBitmap(client.ReadImagePreview(item.Entry.Index)); }
                    catch { if (string.IsNullOrEmpty(item.Entry.Type)) { sound = client.ReadAudioWav(item.Entry.Index); return null; } throw; }
                });
                if (closing || version != previewVersion || client != archive) return;
                if (image != null) {
                    C<Image>("PreviewImage").Source = image; C<TextBlock>("PreviewEmpty").Visibility = Visibility.Collapsed;
                    item.Thumbnail = image; C<TextBlock>("PreviewStatus").Text = "预览尺寸  " + image.PixelWidth + " × " + image.PixelHeight + "\n转换 PNG 时保存完整解码图像。";
                } else if (sound != null) {
                    audio = new MemoryStream(sound); player = new SoundPlayer(audio); C<Button>("PlayAudio").Visibility = Visibility.Visible;
                    C<TextBlock>("PreviewEmpty").Text = "音频资源"; C<TextBlock>("PreviewStatus").Text = "音频已就绪，点击播放。";
                }
            } catch (Exception ex) { if (!closing && version == previewVersion) { C<TextBlock>("PreviewEmpty").Text = item.Type == "封包" ? "封包资源" : "暂无预览"; C<TextBlock>("PreviewStatus").Text = ex.Message; } }
            finally { if (version == previewVersion) previewBusy = false; }
        }
        private readonly HashSet<ResourceItem> failedThumbnails = new HashSet<ResourceItem>();
        private async void LoadOneThumbnail()
        {
            if (!resources || !gridMode || previewBusy || thumbnailBusy || closing || C<Border>("ModalOverlay").Visibility == Visibility.Visible) return;
            var list = C<ListBox>("Tiles"); ResourceItem item = null;
            // Decode only tiles actually visible in the viewport, one request at a time.
            foreach (var candidate in list.Items.OfType<ResourceItem>()) {
                if (candidate.Type != "图像" || candidate.Thumbnail != null || failedThumbnails.Contains(candidate)) continue;
                var container = list.ItemContainerGenerator.ContainerFromItem(candidate) as FrameworkElement;
                if (container == null || !container.IsVisible) continue;
                var bounds = container.TransformToAncestor(list).TransformBounds(new Rect(container.RenderSize));
                if (bounds.Bottom < 0 || bounds.Top > list.ActualHeight) continue;
                item = candidate; break;
            }
            if (item == null) return;
            int version = navigationVersion; var client = archive; thumbnailBusy = true;
            try {
                var image = await Task.Run(() => LoadBitmap(client == null ? File.ReadAllBytes(item.Path) : client.ReadImagePreview(item.Entry.Index), 300));
                if (!closing && version == navigationVersion && client == archive) {
                    item.Thumbnail = image;
                    // Keep only the active page and selected preview in memory.
                    foreach (var other in allItems.Where(i => i.Thumbnail != null && !list.Items.Contains(i) && i != Selected()).ToList()) other.Thumbnail = null;
                }
            } catch { failedThumbnails.Add(item); }
            finally { thumbnailBusy = false; }
        }
        private void ExpandPreview()
        {
            var image = C<Image>("PreviewImage").Source; if (image == null) return;
            ShowModal(C<TextBlock>("PreviewName").Text); C<StackPanel>("ModalBody").Children.Add(new Image { Source = image, Stretch = Stretch.Uniform, MaxHeight = Math.Max(300, window.ActualHeight - 200) });
        }

        private async void SaveSelected(string command, string extension)
        {
            var item = Selected(); if (item == null || item.Directory) return; var client = archive;
            if (client == null && command != "R" && command != "I") { Error("此转换需要先进入封包。"); return; }
            var dialog = new SaveFileDialog { FileName = extension == null ? item.Name : IOPath.ChangeExtension(item.Name, extension), OverwritePrompt = true };
            if (dialog.ShowDialog(window) != true) return;
            SetStatus("正在保存…");
            try {
                await Task.Run(delegate {
                    if (client != null) client.Save(command, item.Entry.Index, dialog.FileName);
                    else if (command == "R") File.Copy(item.Path, dialog.FileName, true);
                    else { var bitmap = LoadBitmap(File.ReadAllBytes(item.Path)); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var output = File.Create(dialog.FileName)) encoder.Save(output); }
                });
                SetStatus("已保存：" + dialog.FileName);
            } catch (Exception ex) { Error(ex.Message); }
        }
        private static string SafeRelative(string name)
        {
            name = name.Replace('\\', '/'); if (name.StartsWith("/") || name.IndexOf(':') >= 0) return null;
            var parts = name.Split('/'); foreach (var part in parts) if (part.Length == 0 || part == "." || part == ".." || part.IndexOfAny(IOPath.GetInvalidFileNameChars()) >= 0) return null;
            return IOPath.Combine(parts);
        }
        private async void ExportAll()
        {
            var client = archive; if (client == null) return; string root = PickFolder(); if (root == null) return;
            var entries = client.Entries.ToList(); exportCancelled = false; C<Button>("CancelExport").Visibility = Visibility.Visible;
            C<Button>("ExportAll").IsEnabled = false; int done = 0, skipped = 0; string failure = "";
            try {
                await Task.Run(delegate {
                    foreach (var entry in entries) {
                        if (exportCancelled) break;
                        try {
                            var relative = SafeRelative(entry.Name); if (relative == null) { skipped++; continue; }
                            string target = IOPath.GetFullPath(IOPath.Combine(root, relative));
                            if (!target.StartsWith(IOPath.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || File.Exists(target)) { skipped++; continue; }
                            Directory.CreateDirectory(IOPath.GetDirectoryName(target)); client.Save("R", entry.Index, target); done++;
                        } catch (Exception ex) { skipped++; failure = ex.Message; }
                        if ((done + skipped) % 10 == 0 && !closing) window.Dispatcher.BeginInvoke((Action)delegate { SetStatus("正在导出  " + (done + skipped) + " / " + entries.Count); });
                    }
                });
                SetStatus((exportCancelled ? "导出已停止" : "导出完成") + "：" + done + " 个；跳过 / 失败 " + skipped + " 个" + (failure.Length == 0 ? "" : "  ·  " + failure));
            } finally { C<Button>("CancelExport").Visibility = Visibility.Collapsed; C<Button>("ExportAll").IsEnabled = archive != null; }
        }
        private void SetStatus(string text) { if (!closing) C<TextBlock>("Status").Text = text; }
        private void Error(string text) { if (closing) return; SetStatus(text); MessageBox.Show(window, text, "galtools", MessageBoxButton.OK, MessageBoxImage.Information); }
        private static void DisposeBackendLater(IArchiveBackend backend) { Task.Run(() => backend.Dispose()); }
    }
}
