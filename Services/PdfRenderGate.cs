using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace XTPdfMergeApp.Services;

public enum PdfRenderPriority { Visible, Thumbnail, Background }

/// <summary>One native caller at a time. Cancellation removes queued work; visible work
/// takes precedence over speculative thumbnails without bypassing PDFium serialization.</summary>
internal sealed class PdfRenderGate
{
    private readonly object _sync = new();
    private readonly LinkedList<Waiter>[] _queues = { new(), new(), new() };
    private readonly int _capacity;
    private int _available;
    public PdfRenderGate(int capacity = 1)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = _available = capacity;
    }
    private sealed class Waiter
    {
        public readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Waiter>? Node;
        public int Priority;
    }

    public async Task WaitAsync(PdfRenderPriority priority, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var waiter = new Waiter { Priority = (int)priority };
        lock (_sync)
        {
            if (_available > 0) { _available--; return; }
            waiter.Node = _queues[waiter.Priority].AddLast(waiter);
        }
        using var registration = token.Register(() =>
        {
            lock (_sync)
            {
                if (waiter.Node == null) return;
                _queues[waiter.Priority].Remove(waiter.Node);
                waiter.Node = null;
                waiter.Completion.TrySetCanceled(token);
            }
        });
        await waiter.Completion.Task.ConfigureAwait(false);
    }

    public void Wait() => WaitAsync(PdfRenderPriority.Visible).GetAwaiter().GetResult();

    public void Release()
    {
        lock (_sync)
        {
            foreach (var queue in _queues)
            {
                if (queue.First is not { } node) continue;
                queue.RemoveFirst();
                node.Value.Node = null;
                node.Value.Completion.TrySetResult();
                return;
            }
            if (_available == _capacity) throw new SemaphoreFullException();
            _available++;
        }
    }
}
