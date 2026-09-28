using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XTPdfMergeApp.Services;

namespace XTPdfMergeApp.Controls
{
    /// <summary>Bảng chọn dấu (docs/UI_REDESIGN.md, mockup 12): Standard / Mine, New stamp…, Import image…, tên + ngày, độ đục.</summary>
    public partial class StampPopup : UserControl
    {
        public StampPopup()
        {
            InitializeComponent();
            Rebuild();
        }

        /// <summary>Chọn 1 dấu: (mẫu, độ đục %, thêm tên + ngày?).</summary>
        internal event Action<StampDefinition, int, bool>? StampChosen;

        private void Tab_Checked(object sender, RoutedEventArgs e) { if (IsLoaded || Grid != null) Rebuild(); }

        private void Rebuild()
        {
            if (Grid == null) return;
            Grid.Children.Clear();
            bool mine = MineTab.IsChecked == true;
            var list = mine ? StampLibrary.Mine : StampLibrary.Standard;
            foreach (var definition in list) Grid.Children.Add(Tile(definition, mine));
            EmptyMine.Visibility = mine && list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private FrameworkElement Tile(StampDefinition definition, bool removable)
        {
            var color = (Color)ColorConverter.ConvertFromString(definition.Color.Length > 0 ? definition.Color : "#C0392B");
            var brush = new SolidColorBrush(color);
            FrameworkElement content;
            if (definition.IsImage)
            {
                var image = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(6) };
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(definition.ImagePath);
                    bitmap.DecodePixelWidth = 160;
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    image.Source = bitmap;
                }
                catch { }
                content = image;
            }
            else
            {
                content = new TextBlock
                {
                    Text = definition.Text, FontWeight = FontWeights.Bold, FontSize = 12, Foreground = brush,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 4, 0)
                };
            }
            var tile = new Border
            {
                Height = 64, Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(2),
                BorderBrush = definition.IsImage ? (Brush)FindResource("Ui.Border") : brush, Cursor = Cursors.Hand, Child = content,
                ToolTip = definition.IsImage ? "Image stamp" : definition.Text
            };
            tile.SetResourceReference(Border.BackgroundProperty, "Ui.Surface");
            tile.MouseEnter += (_, _) => tile.SetResourceReference(Border.BackgroundProperty, "Ui.Hover");
            tile.MouseLeave += (_, _) => tile.SetResourceReference(Border.BackgroundProperty, "Ui.Surface");
            tile.MouseLeftButtonUp += (_, _) => StampChosen?.Invoke(definition, (int)OpacitySlider.Value, SignatureBox.IsChecked == true);
            if (removable)
            {
                var menu = new ContextMenu();
                var remove = new MenuItem { Header = "Remove this stamp" };
                remove.Click += (_, _) => { StampLibrary.Remove(definition); Rebuild(); };
                menu.Items.Add(remove);
                tile.ContextMenu = menu;
            }
            return tile;
        }

        private void NewStamp_Click(object sender, RoutedEventArgs e)
        {
            var definition = NewStampWindow.Ask(Window.GetWindow(this));
            if (definition == null) return;
            StampLibrary.Add(definition);
            MineTab.IsChecked = true;
            Rebuild();
        }

        private void ImportImage_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.OpenFileDialog { Title = "Import a stamp image", Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp" };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            try
            {
                string copy = StampLibrary.ImportImage(dlg.FileName);
                StampLibrary.Add(new StampDefinition(StampDefinition.ImageKind, System.IO.Path.GetFileNameWithoutExtension(dlg.FileName), "", "", copy));
                MineTab.IsChecked = true;
                Rebuild();
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), "Could not import the image:\n" + ex.Message, "Import image", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
