using System;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace XTPdfMergeApp.Services;

/// <summary>Pan requests are coalesced, not debounced: continued scrolling must not
/// starve newly exposed pixels. Resolution changes wait briefly for zoom to settle.
/// The owner cancels obsolete native work separately.</summary>
internal sealed class ViewportRenderScheduler : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Func<Task> _render;
    private bool _dirty, _running, _disposed;
    private DateTime _notBefore;

    public ViewportRenderScheduler(Dispatcher dispatcher, Func<Task> render)
    {
        _render = render;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        _timer.Tick += Tick;
    }

    public void Request(bool resolutionChanged)
    {
        if (_disposed) return;
        _dirty = true;
        if (resolutionChanged)
        {
            _notBefore = DateTime.UtcNow.AddMilliseconds(120);
            _timer.Stop();
        }
        if (!_running && !_timer.IsEnabled) Arm();
    }

    public void Cancel()
    {
        _timer.Stop();
        _dirty = false;
        _notBefore = default;
    }

    private void Arm()
    {
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(16, (_notBefore - DateTime.UtcNow).TotalMilliseconds));
        _timer.Start();
    }

    private async void Tick(object? sender, EventArgs args)
    {
        _timer.Stop();
        if (_disposed || _running || !_dirty) return;
        _dirty = false;
        _running = true;
        try { await _render(); }
        finally
        {
            _running = false;
            if (!_disposed && _dirty) Arm();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Cancel();
        _timer.Tick -= Tick;
    }
}
