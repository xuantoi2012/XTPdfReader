using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;

namespace XTPdfMergeApp.Services;

/// <summary>UI-thread-only work queue. Feed a bounded amount of new tile visuals to
/// WPF each rendering callback instead of installing an entire batch on the dispatcher.</summary>
internal sealed class FramePresentationQueue : IDisposable
{
    private readonly Queue<(Action Present, CancellationToken Token, TaskCompletionSource Done, long Queued)> _queue = new();
    private bool _subscribed;
    private readonly Action<Exception> _onError;
    public FramePresentationQueue(Action<Exception> onError) => _onError = onError;
    public int PendingCount => _queue.Count;
    public double MaxBatchMilliseconds { get; private set; }

    public Task Enqueue(Action action, CancellationToken token)
    {
        if (token.IsCancellationRequested) return Task.CompletedTask;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue((action, token, done, Stopwatch.GetTimestamp()));
        if (!_subscribed) { CompositionTarget.Rendering += OnRendering; _subscribed = true; }
        return done.Task;
    }

    private void OnRendering(object? sender, EventArgs args) => DrainFrame();

    internal void DrainFrame()
    {
        var start = Stopwatch.GetTimestamp();
        int processed = 0;
        while (_queue.Count > 0 && processed < 4 && Stopwatch.GetElapsedTime(start).TotalMilliseconds < 4)
        {
            var item = _queue.Dequeue();
            try
            {
                if (!item.Token.IsCancellationRequested)
                {
                    RenderDiagnostics.PresentationWait.Record(item.Queued);
                    long applyStart = Stopwatch.GetTimestamp();
                    item.Present();
                    RenderDiagnostics.PresentationWork.Record(applyStart);
                }
            }
            catch (Exception ex) { _onError(ex); }
            finally { item.Done.TrySetResult(); }
            processed++;
        }
        MaxBatchMilliseconds = Math.Max(MaxBatchMilliseconds, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        if (_queue.Count == 0) Unsubscribe();
    }

    public void Clear()
    {
        while (_queue.TryDequeue(out var item)) item.Done.TrySetResult();
        Unsubscribe();
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        CompositionTarget.Rendering -= OnRendering;
        _subscribed = false;
    }
    public void Dispose() => Clear();
}
