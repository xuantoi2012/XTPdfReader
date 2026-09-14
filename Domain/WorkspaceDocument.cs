using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace XTPdfMergeApp.Domain;

internal sealed class WorkspaceDocument : INotifyPropertyChanged
{
    private readonly HashSet<PagePlacement> _subscribedPages = [];
    private readonly HashSet<PagePlacement> _loadedPages = [];
    public Guid DocumentId { get; init; } = Guid.NewGuid();
    public required string SourcePath { get; init; }
    private string? _displayName;

    public string FileName => _displayName ?? Path.GetFileName(SourcePath);
    public PageCollection Pages { get; } = new();
    public int LoadedThumbnailCount => _loadedPages.Count;
    public int ThumbnailLoadPercent => Pages.Count == 0 ? 100 : LoadedThumbnailCount * 100 / Pages.Count;

    /// <summary>True từ lúc user thả/chọn file tới khi đếm xong số trang — card hiện NGAY
    /// (không chờ đếm trang xong) ở trạng thái "đang mở", tránh cảm giác trễ giữa lúc status bar
    /// đã báo "đang tải" và lúc thật sự thấy có gì đó xuất hiện trên màn hình.</summary>
    public bool IsOpening { get; private set; }
    public string? LoadError { get; private set; }
    public bool HasLoadError => LoadError != null;

    /// <summary>Loading indicator covers document opening. Thumbnails are intentionally
    /// lazy, so unvisited pages must not keep the document in a permanent loading state.</summary>
    public bool IsThumbnailLoading => !HasLoadError && IsOpening;

    public string HeaderText
    {
        get
        {
            if (LoadError != null) return $"{FileName}  · {LoadError}";
            if (IsOpening) return $"{FileName}  · Đang mở…";
            return $"{FileName}  ({Pages.Count} trang)";
        }
    }

    public void SetOpening(bool opening)
    {
        if (IsOpening == opening) return;
        IsOpening = opening;
        Notify();
        Notify(nameof(HeaderText));
        Notify(nameof(IsThumbnailLoading));
    }

    public void SetLoadError(string? error)
    {
        LoadError = error;
        Notify();
        Notify(nameof(HasLoadError));
        Notify(nameof(HeaderText));
        Notify(nameof(IsThumbnailLoading));
    }

    public WorkspaceDocument()
    {
        Pages.CollectionChanged += Pages_CollectionChanged;
    }

    public void SetDisplayName(string name)
    {
        _displayName = name;
        Notify(nameof(FileName));
        Notify(nameof(HeaderText));
    }

    internal string? CaptureDisplayName() => _displayName;

    internal void RestoreDisplayName(string? name)
    {
        _displayName = name;
        Notify(nameof(FileName));
        Notify(nameof(HeaderText));
    }

    private void Pages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (PagePlacement page in e.OldItems)
            {
                page.PropertyChanged -= Page_PropertyChanged;
                _subscribedPages.Remove(page);
            }

        if (e.NewItems != null)
            foreach (PagePlacement page in e.NewItems)
                if (_subscribedPages.Add(page))
                    page.PropertyChanged += Page_PropertyChanged;

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var page in _subscribedPages.Where(page => !Pages.Contains(page)).ToList())
            {
                page.PropertyChanged -= Page_PropertyChanged;
                _subscribedPages.Remove(page);
            }

            foreach (var page in Pages)
                if (_subscribedPages.Add(page))
                    page.PropertyChanged += Page_PropertyChanged;
        }

        RefreshPages();
    }

    private void Page_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PagePlacement.Thumbnail)) return;
        if (sender is not PagePlacement page || !page.HasLoadedThumbnailOnce || !_loadedPages.Add(page)) return;
        Notify(nameof(LoadedThumbnailCount));
        Notify(nameof(ThumbnailLoadPercent));
        Notify(nameof(HeaderText));
        Notify(nameof(IsThumbnailLoading));
    }

    private void RefreshPages()
    {
        _loadedPages.Clear();
        foreach (var page in Pages)
            if (page.HasLoadedThumbnailOnce) _loadedPages.Add(page);
        Notify(nameof(LoadedThumbnailCount));
        Notify(nameof(ThumbnailLoadPercent));
        Notify(nameof(HeaderText));
        Notify(nameof(IsThumbnailLoading));
        for (int i = 0; i < Pages.Count; i++)
        {
            Pages[i].OwnerSourcePath = SourcePath;
            Pages[i].Index = i + 1;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
