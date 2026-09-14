using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace XTPdfMergeApp.Domain;

/// <summary>
/// Một lần xuất hiện của trang nguồn trong workspace. Nhiều placement có thể cùng
/// trỏ tới một SourcePage; bitmap chỉ là visual state tạm và không phải dữ liệu PDF.
/// </summary>
internal sealed class PagePlacement : INotifyPropertyChanged
{
    public Guid PlacementId { get; init; } = Guid.NewGuid();
    public Guid SourcePageId { get; init; }
    public required string SourcePath { get; init; }
    public required int PageNumber { get; init; }

    public int Rotation { get; set; }
    public string? Bookmark { get; set; }

    private string? _ownerSourcePath;
    public string? OwnerSourcePath
    {
        get => _ownerSourcePath;
        set
        {
            if (string.Equals(_ownerSourcePath, value, StringComparison.OrdinalIgnoreCase)) return;
            _ownerSourcePath = value;
            Notify();
            Notify(nameof(IsImported));
            Notify(nameof(PageLabel));
        }
    }

    public bool IsImported => !string.IsNullOrWhiteSpace(OwnerSourcePath) &&
        !string.Equals(SourcePath, OwnerSourcePath, StringComparison.OrdinalIgnoreCase);

    private int _index;
    public int Index { get => _index; set { _index = value; Notify(); } }

    public string FileName => Path.GetFileName(SourcePath);
    public string PageLabel => IsImported
        ? $"Nguồn: {FileName} · Trang {PageNumber}"
        : $"{FileName} · Trang {PageNumber}";

    // Visuals and byte-budgeted caches keep images alive. Placements may remain in undo
    // history indefinitely and must not retain hundreds of megabytes of render output.
    private WeakReference<BitmapSource>? _thumbnail;
    public BitmapSource? Thumbnail
    {
        get => _thumbnail != null && _thumbnail.TryGetTarget(out var bitmap) ? bitmap : null;
        set
        {
            _thumbnail = value == null ? null : new WeakReference<BitmapSource>(value);
            if (value != null) HasLoadedThumbnailOnce = true;
            Notify();
            Notify(nameof(ReaderDisplayBitmap));
        }
    }
    public bool ThumbnailLoadQueued { get; set; }

    /// <summary>Historical progress, independent of whether the bounded image cache
    /// still owns this page's thumbnail.</summary>
    public bool HasLoadedThumbnailOnce { get; private set; }

    private WeakReference<BitmapSource>? _readerBitmap;
    public BitmapSource? ReaderBitmap
    {
        get => _readerBitmap != null && _readerBitmap.TryGetTarget(out var bitmap) ? bitmap : null;
        set { _readerBitmap = value == null ? null : new WeakReference<BitmapSource>(value); Notify(); Notify(nameof(ReaderDisplayBitmap)); }
    }
    public BitmapSource? ReaderDisplayBitmap => ReaderBitmap ?? Thumbnail;
    public bool ReaderBitmapLoadQueued { get; set; }

    private double? _aspectRatio;
    /// <summary>Height/Width thật của trang nguồn (PDFium FPDF_GetPageWidth/Height), biết được RẺ
    /// hơn nhiều so với chờ render bitmap — dùng để placeholder height (continuous reader) khớp đúng
    /// tỉ lệ trang thật ngay cả khi chưa có Thumbnail/ReaderBitmap.</summary>
    public double? AspectRatio
    {
        get => _aspectRatio;
        set { _aspectRatio = value; Notify(); }
    }
    public bool AspectRatioLoadQueued { get; set; }

    public PagePlacement Copy() => new()
    {
        SourcePageId = SourcePageId,
        SourcePath = SourcePath,
        PageNumber = PageNumber,
        Rotation = Rotation,
        Bookmark = Bookmark,
        Thumbnail = Thumbnail
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
