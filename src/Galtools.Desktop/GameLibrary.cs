using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Galtools
{
    internal sealed partial class DesktopApp
    {
        private void SetLibraryMode(bool enabled)
        {
            libraryMode = enabled;
            C<Grid>("FlowColumns").Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
            C<ScrollViewer>("LibraryViewport").Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            C<Button>("PauseWall").Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
            C<Button>("ModeFlow").Style = (Style)window.FindResource(enabled ? "SoftButton" : "PrimaryButton");
            C<Button>("ModeLibrary").Style = (Style)window.FindResource(enabled ? "PrimaryButton" : "SoftButton");
            C<TextBlock>("WallHint").Text = enabled ? "按名称浏览全部游戏  ·  点击封面查看详情" : "点击卡片查看详情  ·  按序蛇形流动 · 首尾循环";
            if (enabled) LayoutLibrary(); else LayoutWall();
        }

        private void LayoutLibrary()
        {
            if (!libraryMode) return;
            var grid = C<UniformGrid>("LibraryGrid");
            double width = C<Grid>("WallViewport").ActualWidth;
            grid.Columns = Math.Max(2, Math.Min(9, (int)Math.Floor(Math.Max(360, width) / 165)));
            if (grid.Children.Count == gameViews.Count && gameViews.Count > 0) {
                bool same = true;
                for (int i = 0; i < gameViews.Count; i++)
                    if (!ReferenceEquals((grid.Children[i] as FrameworkElement).DataContext, gameViews[i])) { same = false; break; }
                if (same) return;
            }
            grid.Children.Clear();
            foreach (var view in gameViews) {
                var tile = new Border { Background = (Brush)window.FindResource("CardSurface"), BorderBrush = (Brush)window.FindResource("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(11), Margin = new Thickness(6), Cursor = Cursors.Hand, DataContext = view, ClipToBounds = true };
                var body = new Grid(); body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(178) }); body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
                var art = new Border { Background = (Brush)window.FindResource("SoftFill"), ClipToBounds = true };
                var image = new Image { Source = view.Cover, Stretch = Stretch.UniformToFill };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                art.Child = image; body.Children.Add(art);
                var title = new TextBlock { Text = view.Title, FontWeight = FontWeights.SemiBold, FontSize = 12, Margin = new Thickness(8, 9, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = view.Title };
                Grid.SetRow(title, 1); body.Children.Add(title); tile.Child = body;
                tile.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e) { ShowGame(((GameView)((Border)sender).DataContext).Record); e.Handled = true; };
                tile.MouseEnter += delegate { tile.BorderBrush = B("#A9CAFF"); };
                tile.MouseLeave += delegate { tile.BorderBrush = (Brush)window.FindResource("Line"); };
                grid.Children.Add(tile);
            }
        }
    }
}
