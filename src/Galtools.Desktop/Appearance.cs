using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Serialization;
using Microsoft.Win32;

namespace Galtools
{
    [Serializable]
    public sealed class UiPreferences
    {
        public bool DarkTheme { get; set; }
        public List<string> SidebarImages { get; set; }
        public UiPreferences() { SidebarImages = new List<string>(); }
    }

    internal static class UiSettingsStore
    {
        private static string FilePath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "settings.xml"); } }
        internal static string ImageFolder { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "sidebar-backgrounds"); } }
        internal static UiPreferences Load()
        {
            try {
                if (!File.Exists(FilePath)) return new UiPreferences();
                using (var stream = File.OpenRead(FilePath)) {
                    var value = (UiPreferences)new XmlSerializer(typeof(UiPreferences)).Deserialize(stream);
                    if (value.SidebarImages == null) value.SidebarImages = new List<string>();
                    return value;
                }
            } catch { return new UiPreferences(); }
        }
        internal static void Save(UiPreferences value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            string temporary = FilePath + ".partial";
            using (var stream = File.Create(temporary)) new XmlSerializer(typeof(UiPreferences)).Serialize(stream, value);
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null);
            else File.Move(temporary, FilePath);
        }
        internal static string BackgroundPath(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name) return null;
            return Path.Combine(ImageFolder, name);
        }
    }

    internal sealed class SidebarImageView
    {
        public string Name { get; set; }
        public string Detail { get; set; }
        public string FileName { get; set; }
        public BitmapSource Thumbnail { get; set; }
    }

    internal sealed partial class DesktopApp
    {
        private UiPreferences preferences;
        private static bool darkTheme;
        private DispatcherTimer sidebarTimer;
        private int sidebarImageIndex;

        private static string DarkColor(string color)
        {
            switch (color.ToUpperInvariant()) {
                case "#1D2C49": return "#EDF2FF";
                case "#7B8AA3": case "#7D8BA5": case "#94A6C7": return "#A6B7D2";
                case "#E2E9F5": return "#364966";
                case "#A9CAFF": return "#8BAAFF";
                case "#FCEBF3": return "#493148";
                case "#CE6D99": case "#D078A0": return "#F0A0C7";
                case "#F7F9FD": return "#20304B";
                case "#77A4E7": case "#468CFA": return "#9FC0FF";
                case "#FFFFFF": return "#17243C";
                case "#EAF3FF": return "#2B416A";
                case "#F5F1FE": return "#332F55";
                case "#8470BE": return "#C7B7F9";
                case "#E9E0FA": return "#534977";
                default: return color;
            }
        }

        private void SetThemeColor(string key, string light, string dark)
        {
            var resources = Application.Current.Resources.MergedDictionaries[0];
            resources[key] = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(darkTheme ? dark : light));
        }
        private void Gradient(string key, string lightStart, string lightEnd, string darkStart, string darkEnd)
        {
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            brush.GradientStops.Add(new GradientStop((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(darkTheme ? darkStart : lightStart), 0));
            brush.GradientStops.Add(new GradientStop((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(darkTheme ? darkEnd : lightEnd), 1));
            Application.Current.Resources.MergedDictionaries[0][key] = brush;
        }
        private void ApplyTheme()
        {
            SetThemeColor("Ink", "#1D2C49", "#EDF2FF"); SetThemeColor("Muted", "#76839B", "#A8B8D2");
            SetThemeColor("Line", "#E6EAF4", "#34435D"); SetThemeColor("Blue", "#468CFA", "#8CB8FF");
            SetThemeColor("Pink", "#E977A9", "#F19CC5"); SetThemeColor("Violet", "#9380D8", "#C4B3FF");
            SetThemeColor("Surface", "#F7F8FD", "#0C1426"); SetThemeColor("CardSurface", "#FFFFFF", "#18243B");
            SetThemeColor("SidebarBase", "#FCFDFF", "#111C32"); SetThemeColor("SidebarOverlay", "#28FFFFFF", "#330C1830");
            SetThemeColor("SidebarNote", "#EAF2FF", "#263B60"); SetThemeColor("WallFill", "#F3F5FC", "#101C30");
            SetThemeColor("NavCard", "#F2FFFFFF", "#F2FFFFFF"); SetThemeColor("NavCardActive", "#FFFFFFFF", "#FFFFFFFF");
            SetThemeColor("NavCardText", "#263653", "#263653"); SetThemeColor("NavCardAccent", "#286FE0", "#286FE0");
            SetThemeColor("NavCardBorder", "#D8FFFFFF", "#D8FFFFFF"); SetThemeColor("NavCardActiveBorder", "#93BBFF", "#93BBFF");
            SetThemeColor("InputSurface", "#FFFFFF", "#1B2943"); SetThemeColor("PopupSurface", "#FFFFFF", "#202F4A");
            SetThemeColor("HeaderSurface", "#F7F9FE", "#253650"); SetThemeColor("AltSurface", "#FCFDFF", "#1C2B44");
            SetThemeColor("HoverSurface", "#F2F6FF", "#2B405F"); SetThemeColor("SelectedSurface", "#EAF3FF", "#2D4972");
            SetThemeColor("NavActive", "#EAF2FF", "#2A4168"); SetThemeColor("Faint", "#96A5BF", "#91A6C8");
            SetThemeColor("PinkSoft", "#FBEAF2", "#432D49"); SetThemeColor("SoftBlue", "#EDF4FF", "#273E61");
            SetThemeColor("SoftPink", "#FFF0F6", "#432C46"); SetThemeColor("SoftViolet", "#F5F1FE", "#343052");
            SetThemeColor("ScrollThumbFill", "#CBD6E8", "#536783");
            Gradient("PrimaryFill", "#5E9FFF", "#337BEF", "#8B73F0", "#437BDE");
            Gradient("SoftFill", "#EEF5FF", "#FCF0F7", "#233D62", "#463550");
            if (C<DataGrid>("Files") != null) C<DataGrid>("Files").HorizontalGridLinesBrush = (Brush)window.FindResource("Line");
            if (C<Image>("SidebarBackgroundImage") != null) C<Image>("SidebarBackgroundImage").Opacity = 1;
        }
        private void WireSettings()
        {
            C<Button>("LightTheme").Click += delegate { SetTheme(false); };
            C<Button>("DarkTheme").Click += delegate { SetTheme(true); };
            C<Button>("AddSidebarImages").Click += delegate { AddSidebarImages(); };
            C<Button>("RemoveSidebarImage").Click += delegate { RemoveSidebarImage(); };
            C<Button>("ClearSidebarImages").Click += delegate { ClearSidebarImages(); };
            C<ListBox>("SidebarImages").SelectionChanged += delegate { var item = C<ListBox>("SidebarImages").SelectedItem as SidebarImageView; if (item != null) { sidebarImageIndex = preferences.SidebarImages.IndexOf(item.FileName); ShowSidebarImage(false); } };
            UpdateThemeButtons();
        }
        private void UpdateThemeButtons()
        {
            C<Button>("LightTheme").Style = (Style)window.FindResource(darkTheme ? "SoftButton" : "PrimaryButton");
            C<Button>("DarkTheme").Style = (Style)window.FindResource(darkTheme ? "PrimaryButton" : "SoftButton");
        }
        private void SetTheme(bool dark)
        {
            if (darkTheme == dark) return;
            darkTheme = dark; preferences.DarkTheme = dark;
            ApplyTheme(); UpdateThemeButtons(); UpdateNavigation();
            C<Button>("SaveMp3").Background = B("#F5F1FE"); C<Button>("SaveMp3").Foreground = B("#8470BE"); C<Button>("SaveMp3").BorderBrush = B("#E9E0FA");
            RefreshGames(); SetView(gridMode); SavePreferences();
        }
        private void SavePreferences()
        {
            try { UiSettingsStore.Save(preferences); } catch (Exception ex) { Error("保存设置失败：" + ex.Message); }
        }
        private void ShowSettings()
        {
            settings = true; resources = false;
            C<Grid>("GamesPage").Visibility = Visibility.Collapsed;
            C<Grid>("ResourcesPage").Visibility = Visibility.Collapsed;
            C<Grid>("SettingsPage").Visibility = Visibility.Visible;
            UpdateNavigation();
        }
        private void UpdateNavigation()
        {
            C<Button>("NavGames").Tag = !resources && !settings ? "active" : "";
            C<Button>("NavResources").Tag = resources ? "active" : "";
            C<Button>("NavSettings").Tag = settings ? "active" : "";
            ButtonContent(C<Button>("NavGames"), "game", "游戏管理");
            ButtonContent(C<Button>("NavResources"), "folder", "资源浏览");
            ButtonContent(C<Button>("NavSettings"), "settings", "设置");
        }
        private static BitmapSource ReadSidebarBitmap(string path, int width)
        {
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = width; image.UriSource = new Uri(path, UriKind.Absolute); image.EndInit(); image.Freeze(); return image;
        }
        private void RefreshSidebarImages()
        {
            var views = new List<SidebarImageView>();
            foreach (var file in preferences.SidebarImages) {
                var path = UiSettingsStore.BackgroundPath(file);
                if (path == null || !File.Exists(path)) continue;
                try { views.Add(new SidebarImageView { FileName = file, Name = DisplayImageName(file), Detail = DesktopApp.SizeText(new FileInfo(path).Length), Thumbnail = ReadSidebarBitmap(path, 180) }); }
                catch { /* An invalid image remains removable in settings. */ views.Add(new SidebarImageView { FileName = file, Name = file, Detail = "无法读取图片" }); }
            }
            C<ListBox>("SidebarImages").ItemsSource = views;
            C<TextBlock>("SidebarImageCount").Text = views.Count == 0 ? "正在使用默认背景" : views.Count + " 张背景图片 · 每 5 秒轮换";
            if (sidebarImageIndex >= preferences.SidebarImages.Count) sidebarImageIndex = 0;
            ShowSidebarImage(false);
        }
        private void ShowSidebarImage(bool animate)
        {
            var target = C<Image>("SidebarBackgroundImage");
            var subject = C<Image>("SidebarSubjectImage");
            var tint = C<Border>("SidebarTint");
            for (int tries = 0; tries < preferences.SidebarImages.Count; tries++) {
                var path = UiSettingsStore.BackgroundPath(preferences.SidebarImages[sidebarImageIndex]);
                if (TryShowSidebarPicture(path, animate)) return;
                sidebarImageIndex = (sidebarImageIndex + 1) % preferences.SidebarImages.Count;
            }
            if (TryShowSidebarPicture(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "sidebar-default.png"), animate)) return;
            target.Source = null; subject.Source = null; tint.Visibility = Visibility.Collapsed;
        }
        private bool TryShowSidebarPicture(string path, bool animate)
        {
            if (path == null || !File.Exists(path)) return false;
            try {
                var picture = ReadSidebarBitmap(path, 650);
                var target = C<Image>("SidebarBackgroundImage");
                var subject = C<Image>("SidebarSubjectImage");
                target.Source = picture; subject.Source = picture;
                C<Border>("SidebarTint").Visibility = Visibility.Visible;
                if (animate) {
                    target.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(500)));
                    subject.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(500)));
                } else {
                    target.BeginAnimation(UIElement.OpacityProperty, null); subject.BeginAnimation(UIElement.OpacityProperty, null);
                    target.Opacity = 1; subject.Opacity = 1;
                }
                return true;
            } catch { return false; }
        }
        private void StartSidebarTimer()
        {
            sidebarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            sidebarTimer.Tick += delegate { if (preferences.SidebarImages.Count > 1) { sidebarImageIndex = (sidebarImageIndex + 1) % preferences.SidebarImages.Count; ShowSidebarImage(true); } };
            sidebarTimer.Start();
        }
        private void AddSidebarImages()
        {
            var dialog = new OpenFileDialog { Title = "选择左栏背景图片", Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|全部文件|*.*", Multiselect = true };
            if (dialog.ShowDialog(window) != true) return;
            int added = 0;
            try { Directory.CreateDirectory(UiSettingsStore.ImageFolder); } catch (Exception ex) { Error("无法创建背景目录：" + ex.Message); return; }
            foreach (var source in dialog.FileNames) {
                try {
                    ReadSidebarBitmap(source, 650);
                    string originalName = Path.GetFileNameWithoutExtension(source);
                    if (originalName.Length > 80) originalName = originalName.Substring(0, 80);
                    string name = Guid.NewGuid().ToString("N") + "_" + originalName + Path.GetExtension(source).ToLowerInvariant();
                    File.Copy(source, UiSettingsStore.BackgroundPath(name));
                    preferences.SidebarImages.Add(name); added++;
                } catch (Exception ex) { Error("添加背景图片失败：" + ex.Message); }
            }
            if (added > 0) { sidebarImageIndex = preferences.SidebarImages.Count - added; SavePreferences(); RefreshSidebarImages(); }
        }
        private static string DisplayImageName(string stored)
        {
            int separator = stored.IndexOf('_');
            return separator >= 0 ? stored.Substring(separator + 1) : stored;
        }
        private void RemoveSidebarImage()
        {
            var item = C<ListBox>("SidebarImages").SelectedItem as SidebarImageView;
            if (item == null) return;
            preferences.SidebarImages.Remove(item.FileName);
            var path = UiSettingsStore.BackgroundPath(item.FileName);
            try { if (path != null && File.Exists(path)) File.Delete(path); } catch (Exception ex) { Error("删除背景图片失败：" + ex.Message); }
            SavePreferences(); RefreshSidebarImages();
        }
        private void ClearSidebarImages()
        {
            foreach (var name in preferences.SidebarImages) {
                var path = UiSettingsStore.BackgroundPath(name);
                try { if (path != null && File.Exists(path)) File.Delete(path); } catch { }
            }
            preferences.SidebarImages.Clear(); SavePreferences(); RefreshSidebarImages();
        }
    }
}

