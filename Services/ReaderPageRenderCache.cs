using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace XTPdfMergeApp.Services;

/// <summary>Shares live page renders. A cancelled or lower-priority request must not
/// trap a visible page on its thumbnail, or repopulate the cache after invalidation.</summary>
internal sealed class ReaderPageRenderCache
{
    private readonly object _sync = new();
    private readonly BitmapMemoryCache<(string Path, int Page, int Width, string Layers)> _images;
    private readonly Dictionary<(string Path, int Page, int Width, string Layers), Request> _loads = new();
    private readonly Func<(string Path, int Page, int Width, string Layers), PdfRenderPriority, CancellationToken, Task<BitmapSource?>> _render;

    private sealed class Request
    {
        public required CancellationTokenSource Cancellation;
        public required PdfRenderPriority Priority;
        public readonly TaskCompletionSource<BitmapSource?> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public ReaderPageRenderCache(long budget,
        Func<(string Path, int Page, int Width, string Layers), PdfRenderPriority, CancellationToken, Task<BitmapSource?>> render)
    {
        _images = new(budget);
        _render = render;
    }

    public (int Cache, int Inflight, long Bytes) Stats
    {
        get { lock (_sync) return (_images.Count, _loads.Count, _images.Bytes); }
    }

    public Task<BitmapSource?> GetAsync((string Path, int Page, int Width, string Layers) key,
        PdfRenderPriority priority, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_images.TryGetValue(key, out var bitmap)) return Task.FromResult<BitmapSource?>(bitmap);
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
            lock (_sync)
            {
                if (!request.Cancellation.IsCancellationRequested && _loads.TryGetValue(key, out var current) && ReferenceEquals(current, request))
                {
                    if (bitmap != null) _images.Set(key, bitmap);
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

    public void Invalidate(Func<(string Path, int Page, int Width, string Layers), bool> matches)
    {
        lock (_sync)
        {
            _images.RemoveWhere(matches);
            foreach (var key in _loads.Keys.Where(matches).ToArray())
            {
                _loads[key].Cancellation.Cancel();
                _loads.Remove(key);
            }
        }
    }

    public void Clear() => Invalidate(_ => true);
}
