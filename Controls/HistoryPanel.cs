using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using XTPdfMergeApp.Services;
using DocumentGroup = XTPdfMergeApp.Domain.WorkspaceDocument;

namespace XTPdfMergeApp.Controls;

/// <summary>
/// Tab History của panel trái: các lần lưu của file đang xem (ai, lúc nào, làm gì), mới nhất ở trên. Lịch sử nằm trong chính file
/// (<see cref="XTHistory"/>) nên cả nhóm cùng thấy. Chuột phải 1 dòng → "Save the version before this change as…" lấy lại phiên bản trước lần lưu đó.
/// </summary>
public sealed class HistoryPanel : UserControl
{
    private readonly ListBox _list;
    private readonly TextBlock _header, _empty;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private string? _path;
    private long _stampLength;
    private DateTime _stampTime;
    private int _generation;

    public HistoryPanel()
    {
        Brush R(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12;

        _header = new TextBlock { Margin = new Thickness(14, 0, 14, 8), TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Opacity = 0.75 };
        DockPanel.SetDock(_header, Dock.Top);

        _list = new ListBox { BorderThickness = new Thickness(0, 1, 0, 0), Background = Brushes.Transparent };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.SetResourceReference(Control.BorderBrushProperty, "Ui.Border");
        _list.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "UiRowItem");
        _list.ItemTemplate = BuildTemplate();
        var menu = new ContextMenu();
        var restore = new MenuItem { Header = "Save the version before this change as…" };
        restore.Click += Restore_Click;
        menu.Items.Add(restore);
        _list.ContextMenu = menu;
        _list.ContextMenuOpening += (_, e) => { if (_list.SelectedItem == null) e.Handled = true; };

        _empty = new TextBlock { Margin = new Thickness(14), TextWrapping = TextWrapping.Wrap, Foreground = R("Ui.Muted"), Visibility = Visibility.Collapsed };

        var root = new DockPanel();
        root.Children.Add(_header);
        root.Children.Add(new Grid { Children = { _list, _empty } });
        Content = root;

        _timer.Tick += (_, _) => _ = RefreshAsync(force: false);
        IsVisibleChanged += (_, _) => { if (IsVisible) { _timer.Start(); _ = RefreshAsync(force: true); } else _timer.Stop(); };
    }

    /// <summary>Số dòng lịch sử (null = không có).</summary>
    internal event Action<int?>? CountChanged;

    private static DataTemplate BuildTemplate()
    {
        var stack = new FrameworkElementFactory(typeof(StackPanel));
        stack.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 4, 12, 4));
        var action = new FrameworkElementFactory(typeof(TextBlock));
        action.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(HistoryEntry.Action)));
        action.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        action.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        stack.AppendChild(action);
        var detail = new FrameworkElementFactory(typeof(TextBlock));
        detail.SetBinding(TextBlock.TextProperty, new System.Windows.Data.MultiBinding
        {
            StringFormat = "{0} · {1}",
            Bindings =
            {
                new System.Windows.Data.Binding(nameof(HistoryEntry.TimeText)),
                new System.Windows.Data.Binding(nameof(HistoryEntry.User))
            }
        });
        detail.SetValue(TextBlock.FontSizeProperty, 11.0);
        detail.SetValue(UIElement.OpacityProperty, 0.7);
        detail.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        stack.AppendChild(detail);
        return new DataTemplate { VisualTree = stack };
    }

    /// <summary>File của window đang xem; nhiều file nguồn (window ghép) thì không có lịch sử chung.</summary>
    internal void SetGroup(DocumentGroup? group)
    {
        var paths = group?.Pages.Select(p => p.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
        string? path = paths.Count == 1 ? paths[0] : null;
        if (string.Equals(path, _path, StringComparison.OrdinalIgnoreCase) && path != null) return;
        _path = path;
        _stampLength = -1;
        if (path == null)
        {
            _generation++;
            Show(null, group == null ? "Open a file to see its history." : "A combined window has no single history. Open the file in its own tab.");
            return;
        }
        if (IsVisible) _ = RefreshAsync(force: true);
    }

    /// <summary>Đọc lại lịch sử (bỏ qua nếu file chưa đổi, trừ khi <paramref name="force"/>).</summary>
    internal async Task RefreshAsync(bool force)
    {
        string? path = _path;
        if (path == null) return;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) { Show(null, "The file is not on disk."); return; }
            if (!force && info.Length == _stampLength && info.LastWriteTimeUtc == _stampTime) return;
            _stampLength = info.Length;
            _stampTime = info.LastWriteTimeUtc;
        }
        catch { return; }

        int generation = ++_generation;
        var entries = await Task.Run(() => XTHistory.Read(path));
        if (generation != _generation) return;
        Show(entries.Reverse().ToList(), "No saves recorded in this file yet. Annotations, layer and bookmark changes and page rotation are listed here once saved.");
    }

    private void Show(List<HistoryEntry>? entries, string emptyText)
    {
        bool empty = entries == null || entries.Count == 0;
        _empty.Text = emptyText;
        _empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        _list.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        _list.ItemsSource = entries;
        _header.Text = empty ? "" : $"Last saved by {entries![0].User} · {entries[0].TimeText}";
        CountChanged?.Invoke(empty ? null : entries!.Count);
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_path == null || _list.SelectedItem is not HistoryEntry entry) return;
        using var dialog = new System.Windows.Forms.SaveFileDialog
        {
            Title = "Save the earlier version", Filter = "PDF (*.pdf)|*.pdf", DefaultExt = "pdf",
            FileName = Path.GetFileNameWithoutExtension(_path) + " - before " + entry.TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH-mm") + ".pdf",
            InitialDirectory = Path.GetDirectoryName(_path) is { } dir && Directory.Exists(dir) ? dir : ""
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        string output = Path.GetFullPath(dialog.FileName);
        if (string.Equals(output, Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
        {
            AppDialog.Show(Window.GetWindow(this), "Choose a different file name; the current file is not replaced.", "History", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try { XTHistory.SaveVersion(_path, entry.LengthBefore, output); XTPdfMergeApp.Services.Growl.Success("Saved " + Path.GetFileName(output), Window.GetWindow(this)); }
        catch (Exception ex) { AppDialog.Show(Window.GetWindow(this), "Could not save that version:\n" + ex.Message, "History", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
