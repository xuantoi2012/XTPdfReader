using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using XTPdfMergeApp.Services;
using PageRow = XTPdfMergeApp.Domain.PagePlacement;
using DocumentGroup = XTPdfMergeApp.Domain.WorkspaceDocument;

namespace XTPdfMergeApp.Controls;

internal sealed class MergeOrderRow
{
    internal MergeOrderRow(DocumentGroup document) => Document = document;
    internal DocumentGroup Document { get; }
    public int Order { get; set; }
    public string Name => Document.FileName;
    public string PageCountText => Document.Pages.Count == 1 ? "1 page" : $"{Document.Pages.Count} pages";
    public PageRow? FirstPage => Document.Pages.FirstOrDefault();
}

/// <summary>Chỉ chọn thứ tự các window trước khi mở hộp Save. Nhóm Temp không thể lọt vào đây.</summary>
public partial class MergeOrderWindow : XTStyle.Controls.XTWindow
{
    private readonly ObservableCollection<MergeOrderRow> _rows;
    private readonly Dictionary<PageRow, System.Threading.CancellationTokenSource> _thumbnailRequests = new();
    private Point _dragOrigin;
    private MergeOrderRow? _dragRow;

    internal IReadOnlyList<DocumentGroup> OrderedDocuments { get; private set; } = [];

    internal MergeOrderWindow(IEnumerable<DocumentGroup> documents)
    {
        _rows = new ObservableCollection<MergeOrderRow>(documents.Select(document => new MergeOrderRow(document)));
        UpdateOrderNumbers();
        InitializeComponent();
        OrderList.ItemsSource = _rows;
        Loaded += (_, _) =>
        {
            foreach (var page in _rows.Select(row => row.FirstPage).OfType<PageRow>()) RequestThumbnail(page);
        };
        Closed += (_, _) => CancelThumbnailRequests();
    }

    private void UpdateOrderNumbers()
    {
        for (int index = 0; index < _rows.Count; index++) _rows[index].Order = index + 1;
        if (OrderList != null) OrderList.Items.Refresh();
    }

    private static ListBoxItem? ItemUnder(DependencyObject? source)
    {
        while (source != null && source is not ListBoxItem) source = VisualTreeHelper.GetParent(source);
        return source as ListBoxItem;
    }

    private void OrderList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragOrigin = e.GetPosition(OrderList);
        _dragRow = ItemUnder(e.OriginalSource as DependencyObject)?.DataContext as MergeOrderRow;
    }

    private void OrderList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragRow == null || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(OrderList) - _dragOrigin;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var row = _dragRow;
        _dragRow = null;
        DragDrop.DoDragDrop(OrderList, row, DragDropEffects.Move);
    }

    private void OrderList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(MergeOrderRow)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OrderList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(MergeOrderRow)) is not MergeOrderRow source) return;
        var target = ItemUnder(e.OriginalSource as DependencyObject)?.DataContext as MergeOrderRow;
        int sourceIndex = _rows.IndexOf(source);
        int targetIndex = target == null ? _rows.Count - 1 : _rows.IndexOf(target);
        if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex) return;
        _rows.Move(sourceIndex, targetIndex);
        UpdateOrderNumbers();
        e.Handled = true;
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        OrderedDocuments = _rows.Select(row => row.Document).ToList();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void RequestThumbnail(PageRow page)
    {
        if (page.Thumbnail != null || _thumbnailRequests.ContainsKey(page)) return;
        var cts = new System.Threading.CancellationTokenSource();
        _thumbnailRequests[page] = cts;
        _ = LoadThumbnailAsync(page, cts);
    }

    private async System.Threading.Tasks.Task LoadThumbnailAsync(PageRow page, System.Threading.CancellationTokenSource cts)
    {
        try { await ThumbnailCache.LoadPreviewAsync(page, cts.Token, PdfRenderPriority.Thumbnail); }
        finally
        {
            if (_thumbnailRequests.TryGetValue(page, out var current) && ReferenceEquals(current, cts)) _thumbnailRequests.Remove(page);
            cts.Dispose();
        }
    }

    private void CancelThumbnailRequests()
    {
        foreach (var cts in _thumbnailRequests.Values) cts.Cancel();
        _thumbnailRequests.Clear();
    }
}
