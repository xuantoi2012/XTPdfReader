using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using XTStyle.Controls;

namespace XTCapture
{
    /// <summary>What can be done with a stored capture (shared by the Store list and the editor). The picture always includes what is drawn on it.</summary>
    internal static class StoreActions
    {
        public static void Copy(CaptureStore.Entry entry) => Copy(CaptureStore.LoadFlattened(entry));

        public static void Copy(System.Windows.Media.Imaging.BitmapSource picture)
        {
            CaptureClipboard.Copy(picture);
            ToastWindow.Display("Copied");
        }

        public static void SavePng(CaptureStore.Entry entry, Window owner)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "PNG picture (*.png)|*.png", FileName = entry.Id + ".png", AddExtension = true, DefaultExt = ".png" };
            if (dialog.ShowDialog(owner) != true) return;
            try { File.WriteAllBytes(dialog.FileName, CaptureImaging.EncodePng(CaptureStore.LoadFlattened(entry))); ToastWindow.Display("Saved"); }
            catch (Exception ex) { MessageBox.Show(owner, "Could not save the picture: " + ex.Message, "XT Capture", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        public static void ExportPdf(CaptureStore.Entry entry, Window owner)
            => ExportPdf(entry, CaptureStore.LoadOriginal(entry), CaptureStore.LoadMarkup(entry), owner);

        /// <summary>Asks where, and (when something is drawn) whether the shapes and texts stay PDF annotations.</summary>
        public static void ExportPdf(CaptureStore.Entry entry, System.Windows.Media.Imaging.BitmapSource original, IReadOnlyList<MarkupItem> items, Window owner)
        {
            bool annotations = false;
            if (items.Any(PdfExporter.CanBeAnnotation))
            {
                var answer = MessageBox.Show(owner, "Keep the rectangles, circles, lines, arrows, pen strokes and texts as PDF annotations, so they can still be edited in a PDF viewer?" + Environment.NewLine + Environment.NewLine +
                    "Yes: annotations (numbered markers and mosaics become part of the picture)." + Environment.NewLine + "No: everything becomes part of the picture.",
                    "Export to PDF", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Cancel) return;
                annotations = answer == MessageBoxResult.Yes;
            }
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "PDF file (*.pdf)|*.pdf", FileName = entry.Id + ".pdf", AddExtension = true, DefaultExt = ".pdf" };
            if (dialog.ShowDialog(owner) != true) return;
            try { PdfExporter.Export(original, items, annotations, dialog.FileName); ToastWindow.Display("Exported to PDF"); }
            catch (Exception ex) { MessageBox.Show(owner, "Could not export the PDF: " + ex.Message, "XT Capture", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
    }

    /// <summary>The Store: every capture, newest first, with its picture; copy it, save it as PNG, export it to PDF, delete it, or click the picture to see it large.</summary>
    internal sealed class StoreWindow : XTWindow
    {
        private static StoreWindow? _current;

        /// <summary>Tests: windows open far off screen instead of in the middle of it.</summary>
        internal static bool Offscreen { get; set; }

        private readonly Action _capture;
        private readonly Action _settings;
        private readonly WrapPanel _wrap = new() { Margin = new Thickness(14, 8, 14, 14) };
        private readonly TextBlock _emptyText;
        private readonly TextBlock _summary;

        internal int CardCount => _wrap.Children.Count;

        private StoreWindow(Action capture, Action settings)
        {
            _capture = capture;
            _settings = settings;
            Title = "XT Capture: Store";
            TitleBarMode = TitleBarMode.Dialog;
            Width = 980;
            Height = 660;
            MinWidth = 560;
            MinHeight = 380;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;
            Closed += (_, _) => { if (ReferenceEquals(_current, this)) _current = null; };
            Activated += (_, _) => Reload();

            Brush R(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
            var root = new DockPanel { Background = R("Ui.Surface") };

            var header = new DockPanel { Margin = new Thickness(18, 12, 18, 4), LastChildFill = false };
            _summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = R("Ui.Muted") };
            DockPanel.SetDock(_summary, Dock.Left);
            header.Children.Add(_summary);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(buttons, Dock.Right);
            buttons.Children.Add(MakeButton(CaptureIcons.Capture, "Capture", "Capture the screen now", () => { Hide(); _capture(); }, accent: true));
            buttons.Children.Add(MakeButton(CaptureIcons.Trash, "Clean up…", "Remove the captures older than a number of days", CleanUp));
            buttons.Children.Add(MakeButton(CaptureIcons.Store, "Open folder", "Show the folder in Explorer", OpenFolder));
            buttons.Children.Add(MakeButton(CaptureIcons.Settings, "Settings", "Shortcut, corner button and start with Windows", () => _settings()));
            header.Children.Add(buttons);
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            _emptyText = new TextBlock
            {
                Text = "Nothing captured yet. Press the shortcut or the button in the corner of the screen: every capture is kept here, and you can draw and write on it later.",
                Margin = new Thickness(22, 20, 22, 0), TextWrapping = TextWrapping.Wrap, Foreground = R("Ui.Muted"), Visibility = Visibility.Collapsed
            };
            DockPanel.SetDock(_emptyText, Dock.Top);
            root.Children.Add(_emptyText);
            root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _wrap });
            Content = root;
            Reload();
        }

        /// <summary>Opens the Store (one window); brings it to the front when it is already open.</summary>
        internal static StoreWindow Open(Action capture, Action settings)
        {
            if (_current == null)
            {
                _current = new StoreWindow(capture, settings);
                if (Offscreen) { _current.WindowStartupLocation = WindowStartupLocation.Manual; _current.Left = _current.Top = -32000; }
            }
            _current.Show();
            if (_current.WindowState == WindowState.Minimized) _current.WindowState = WindowState.Normal;
            _current.Activate();
            return _current;
        }

        internal static bool IsOpen => _current != null;
        internal static void CloseCurrent() => _current?.Close();
        internal static void HideCurrent() => _current?.Hide();

        private Border MakeButton(string icon, string text, string tip, Action click, bool accent = false)
        {
            Brush foreground = accent ? Brushes.White : (TryFindResource("Ui.Text") as Brush ?? Brushes.Black);
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(CaptureIcons.Create(icon, foreground));
            content.Children.Add(new TextBlock { Text = text, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = foreground });
            var border = new Border
            {
                Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(6, 0, 0, 0), CornerRadius = new CornerRadius(5), Cursor = Cursors.Hand, ToolTip = tip, Child = content,
                Background = accent ? (TryFindResource("Ui.Accent") as Brush ?? new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB))) : Brushes.Transparent,
                BorderThickness = new Thickness(1), BorderBrush = accent ? Brushes.Transparent : (TryFindResource("Ui.Border") as Brush ?? Brushes.LightGray)
            };
            border.MouseLeftButtonUp += (_, _) => click();
            return border;
        }

        // ── List ─────────────────────────────────────────────────────────

        internal void Reload()
        {
            var entries = CaptureStore.List();
            var shown = _wrap.Children.OfType<Border>().Select(c => c.Tag as string).ToList();
            if (!shown.SequenceEqual(entries.Select(Key)))
            {
                _wrap.Children.Clear();
                foreach (var entry in entries) _wrap.Children.Add(MakeCard(entry));
            }
            _summary.Text = entries.Count == 0 ? "" : $"{entries.Count} capture{(entries.Count == 1 ? "" : "s")}, {CaptureStore.FormatSize(entries.Sum(e => e.Length))}";
            _emptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>A card is rebuilt when its capture changes (a saved drawing renews the list picture).</summary>
        private static string Key(CaptureStore.Entry entry)
        {
            try { return entry.Id + "|" + File.GetLastWriteTimeUtc(entry.ThumbPath).Ticks; }
            catch { return entry.Id; }
        }

        private Border MakeCard(CaptureStore.Entry entry)
        {
            Brush R(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
            var picture = new Image { Stretch = Stretch.Uniform, Height = 130 };
            try { picture.Source = CaptureStore.LoadThumbnail(entry); } catch { /* the picture file is gone: an empty well */ }
            var well = new Border { Background = R("Ui.Hover"), CornerRadius = new CornerRadius(5), Padding = new Thickness(4), Child = picture, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 6) };
            well.MouseLeftButtonUp += (_, _) => EditorWindow.For(entry, this);

            var name = new TextBlock { Text = entry.Created.ToString("dd MMM yyyy  HH:mm:ss"), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = R("Ui.Text") };
            var meta = new TextBlock { Text = (entry.Width > 0 ? $"{entry.Width} × {entry.Height}   " : "") + CaptureStore.FormatSize(entry.Length), FontSize = 11.5, Foreground = R("Ui.Muted"), Margin = new Thickness(0, 1, 0, 6) };
            var actions = new WrapPanel();
            actions.Children.Add(LinkButton("Edit", "Draw and write on it", () => EditorWindow.For(entry, this)));
            actions.Children.Add(LinkButton("Copy", "Copy the picture", () => StoreActions.Copy(entry)));
            actions.Children.Add(LinkButton("Save PNG", "Save the picture as a PNG file", () => StoreActions.SavePng(entry, this)));
            actions.Children.Add(LinkButton("PDF", "Export to a one-page PDF", () => StoreActions.ExportPdf(entry, this)));
            actions.Children.Add(LinkButton("Delete", "Move to the Recycle Bin", () => Delete(entry)));

            var stack = new StackPanel();
            stack.Children.Add(well);
            stack.Children.Add(name);
            stack.Children.Add(meta);
            stack.Children.Add(actions);
            return new Border
            {
                Width = 224, Margin = new Thickness(0, 0, 12, 12), Padding = new Thickness(8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                BorderBrush = R("Ui.Border"), Child = stack, ToolTip = entry.Folder, Tag = Key(entry)
            };
        }

        private TextBlock LinkButton(string text, string tip, Action click)
        {
            var link = new TextBlock { Text = text, Margin = new Thickness(0, 0, 12, 0), Cursor = Cursors.Hand, ToolTip = tip };
            link.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Accent");
            link.MouseLeftButtonUp += (_, _) => click();
            return link;
        }

        // ── Actions ──────────────────────────────────────────────────────

        private void Delete(CaptureStore.Entry entry)
        {
            if (MessageBox.Show(this, "Move this capture to the Recycle Bin?", "XT Capture", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            if (!CaptureStore.Delete(entry)) MessageBox.Show(this, "Could not remove it.", "XT Capture", MessageBoxButton.OK, MessageBoxImage.Warning);
            Reload();
        }

        private void CleanUp()
        {
            var prompt = new DaysPrompt(CaptureSettings.CleanupDays) { Owner = this };
            if (prompt.ShowDialog() != true || prompt.Days is not int days) return;
            CaptureSettings.CleanupDays = days;
            var old = CaptureStore.FindOlderThan(days);
            if (old.Count == 0) { MessageBox.Show(this, $"No capture is older than {days} days.", "XT Capture", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            if (MessageBox.Show(this, $"Move {old.Count} capture{(old.Count == 1 ? "" : "s")} ({CaptureStore.FormatSize(old.Sum(e => e.Length))}) to the Recycle Bin?", "XT Capture",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            int removed = old.Count(CaptureStore.Delete);
            Reload();
            ToastWindow.Display($"{removed} removed");
        }

        private static void OpenFolder()
        {
            try
            {
                Directory.CreateDirectory(CaptureStore.Folder);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + CaptureStore.Folder + "\"") { UseShellExecute = true });
            }
            catch { /* no Explorer: nothing to show */ }
        }
    }

    /// <summary>Asks for a number of days.</summary>
    internal sealed class DaysPrompt : XTWindow
    {
        private readonly TextBox _box;
        public int? Days { get; private set; }

        public DaysPrompt(int initial)
        {
            Title = "Clean up";
            TitleBarMode = TitleBarMode.Dialog;
            Width = 340;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;
            _box = new TextBox { Text = initial.ToString(), Height = 30, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
            var ok = new Button { Content = "OK", Width = 80, Height = 28, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "Cancel", Width = 80, Height = 28, IsCancel = true };
            ok.Click += (_, _) =>
            {
                if (int.TryParse(_box.Text, out int days) && days is >= 1 and <= 3650) { Days = days; DialogResult = true; }
                else _box.SelectAll();
            };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), Children = { ok, cancel } };
            Content = new StackPanel { Margin = new Thickness(18), Children = { new TextBlock { Text = "Remove the captures older than this many days:", TextWrapping = TextWrapping.Wrap }, _box, buttons } };
            Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
        }
    }
}
