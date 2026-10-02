using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XTPdfMergeApp.Services;

/// <summary>Shares live page renders. A cancelled or lower-priority request must not
/// trap a visible page on its thumbnail, or repopulate the cache after invalidation.</summary>
internal sealed class ReaderPageRenderCache
{
    private readonly object _sync = new();
    private readonly BitmapMemoryCache<(string Path, int Page, int Width, string Layers)> _images;
    internal const long PreviewBudgetBytes = 16L * 1024 * 1024;
    private readonly BitmapMemoryCache<(string Path, int Page, int Width, string Layers)> _previews;
    private readonly Dictionary<(string Path, int Page, int Width, string Layers), Request> _loads = new();
    private readonly Func<(string Path, int Page, int Width, string Layers), PdfRenderPriority, CancellationToken, Task<BitmapSource?>> _render;
    internal bool ReuseLargerImages { get; set; } = true;

    private sealed class Request
    {
        public required CancellationTokenSource Cancellation;
        public required PdfRenderPriority Priority;
        public readonly TaskCompletionSource<BitmapSource?> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public ReaderPageRenderCache(long budget,
        Func<(string Path, int Page, int Width, string Layers), PdfRenderPriority, CancellationToken, Task<BitmapSource?>> render,
        long previewBudget = PreviewBudgetBytes)
    {
        _images = new(budget);
        _previews = new(previewBudget);
        _render = render;
    }

    public (int Cache, int Inflight, long Bytes) Stats
    {
        get { lock (_sync) return (_images.Count + _previews.Count, _loads.Count, _images.Bytes + _previews.Bytes); }
    }

    internal (int Count, long Bytes) PreviewStats
    {
        get { lock (_sync) return (_previews.Count, _previews.Bytes); }
    }

    // Display-only fallback: a preview must never satisfy a request for a sharp render.
    internal BitmapSource? TryGetDisplayImage((string Path, int Page, int Width, string Layers) key)
    {
        lock (_sync)
        {
            if (TryGetLocked(key) is { } sharp) return sharp;
            return _previews.TryFind(k => k.Path == key.Path && k.Page == key.Page && k.Layers == key.Layers,
                out _, out var preview) ? preview : null;
        }
    }

    public BitmapSource? TryGet((string Path, int Page, int Width, string Layers) key)
    {
        lock (_sync) return TryGetLocked(key);
    }

    private BitmapSource? TryGetLocked((string Path, int Page, int Width, string Layers) key)
    {
        if (_images.TryGetValue(key, out var bitmap)) return bitmap;
        if (!ReuseLargerImages) return null;
        return _images.TryFind(k => k.Path == key.Path && k.Page == key.Page && k.Layers == key.Layers && k.Width >= key.Width,
            out _, out bitmap) ? bitmap : null;
    }

    public Task<BitmapSource?> GetAsync((string Path, int Page, int Width, string Layers) key,
        PdfRenderPriority priority, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (TryGetLocked(key) is { } bitmap) return Task.FromResult<BitmapSource?>(bitmap);
            if (_loads.TryGetValue(key, out var existing))
            {
                if (!existing.Cancellation.IsCancellationRequested && existing.Priority <= priority)
                    return existing.Completion.Task.WaitAsync(token);
                // Restart at the foreground priority; never attach to the cancelled task.
                existing.Cancellation.Cancel();
            }
            var request = new Request { Cancellation = CancellationTokenSource.CreateLinkedTokenSource(token), Priority = priority };
            _loads[key] = request;
            _ = Task.Run(() => RenderAsync(key, request));
            return request.Completion.Task.WaitAsync(token);
        }
    }

    private async Task RenderAsync((string Path, int Page, int Width, string Layers) key, Request request)
    {
        try
        {
            var token = request.Cancellation.Token;
            token.ThrowIfCancellationRequested();
            var bitmap = await _render(key, request.Priority, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            BitmapSource? preview = null;
            if (bitmap != null && _previews.BudgetBytes > 0)
            {
                try { preview = CreatePreview(bitmap); }
                catch (Exception ex) { Debug.WriteLine($"Reader display preview unavailable: {ex.Message}"); }
            }
            token.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (!request.Cancellation.IsCancellationRequested && _loads.TryGetValue(key, out var current) && ReferenceEquals(current, request))
                {
                    if (bitmap != null) _images.Set(key, bitmap);
                    if (preview != null)
                    {
                        _previews.RemoveWhere(k => k.Path == key.Path && k.Page == key.Page && k.Layers == key.Layers);
                        _previews.Set(key, preview);
                    }
                    _loads.Remove(key);
                }
            }
            token.ThrowIfCancellationRequested();
            request.Completion.TrySetResult(bitmap);
        }
        catch (OperationCanceledException)
        {
            request.Completion.TrySetCanceled();
        }
        catch (Exception ex) { request.Completion.TrySetException(ex); }
        finally
        {
            lock (_sync)
            {
                if (_loads.TryGetValue(key, out var current) && ReferenceEquals(current, request)) _loads.Remove(key);
                request.Cancellation.Dispose();
            }
        }
    }

    private static BitmapSource? CreatePreview(BitmapSource source)
    {
        double scale = Math.Min(1, Math.Min(512d / source.PixelWidth, 1024d / source.PixelHeight));
        if (scale >= 1) return null;
        var transform = new ScaleTransform(scale, scale);
        transform.Freeze();
        var scaled = new TransformedBitmap(source, transform);
        int stride = (scaled.PixelWidth * scaled.Format.BitsPerPixel + 7) / 8;
        var pixels = new byte[stride * scaled.PixelHeight];
        scaled.CopyPixels(pixels, stride, 0);
        // Materialize the small pixels: retaining TransformedBitmap would also retain the full source.
        var preview = BitmapSource.Create(scaled.PixelWidth, scaled.PixelHeight, 96, 96,
            scaled.Format, scaled.Palette, pixels, stride);
        preview.Freeze();
        return preview;
    }

    public void Invalidate(Func<(string Path, int Page, int Width, string Layers), bool> matches)
    {
        lock (_sync)
        {
            _images.RemoveWhere(matches);
            _previews.RemoveWhere(matches);
            foreach (var key in _loads.Keys.Where(matches).ToArray())
            {
                _loads[key].Cancellation.Cancel();
                _loads.Remove(key);
            }
        }
    }

    public void Clear() => Invalidate(_ => true);
}
