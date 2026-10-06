using System;
using System.Threading;
using System.Threading.Tasks;

namespace XTPdfMergeApp.Services;

// Reservations cover output pixels and estimated native/IPC/WPF copies, not document parsing.
// An oversized request runs alone so legitimate visible work cannot deadlock on the limit.
internal sealed class RenderMemoryBudget
{
    private readonly object _sync = new();
    private long _capacity;
    internal void SetCapacity(long capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        lock (_sync) { _capacity = capacity; Signal(); }
    }
    private long _used, _peak;
    private int _foregroundWaiters;
    private TaskCompletionSource _changed = NewSignal();
    internal RenderMemoryBudget(long capacity) => _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal (long Used, long Peak) Stats { get { lock (_sync) return (_used, _peak); } }
    internal async Task<IDisposable> AcquireAsync(long bytes, PdfRenderPriority priority, CancellationToken token)
    {
        if (bytes <= 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        bool foreground = priority == PdfRenderPriority.Visible;
        lock (_sync) { if (foreground) _foregroundWaiters++; }
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                Task changed;
                lock (_sync)
                {
                    if ((foreground || _foregroundWaiters == 0) && (_used == 0 || bytes <= _capacity - _used))
                    { _used += bytes; _peak = Math.Max(_peak, _used); return new Lease(this, bytes); }
                    changed = _changed.Task;
                }
                await changed.WaitAsync(token).ConfigureAwait(false);
            }
        }
        finally { lock (_sync) { if (foreground) _foregroundWaiters--; Signal(); } }
    }
    private void Signal() { var signal = _changed; _changed = NewSignal(); signal.TrySetResult(); }
    private sealed class Lease(RenderMemoryBudget owner, long bytes) : IDisposable
    {
        private RenderMemoryBudget? _owner = owner;
        public void Dispose()
        { var budget = Interlocked.Exchange(ref _owner, null); if (budget == null) return;
            lock (budget._sync) { budget._used -= bytes; budget.Signal(); } }
    }
}
