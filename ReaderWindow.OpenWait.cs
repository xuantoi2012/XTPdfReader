using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace XTPdfMergeApp;

public partial class ReaderWindow
{
    // The wait cursor of a file open: shown on the reader window only (dialogs such as the password prompt keep the normal arrow) from the
    // moment the open starts until the first page has an image on screen (or 30 s have passed / the open produced nothing).

    private DispatcherTimer? _openWaitTimer;
    private DateTime _openWaitDeadline;
    private bool _openWaitShown;

    internal void BeginOpenWait()
    {
        _openWaitShown = false;
        _openWaitDeadline = DateTime.UtcNow.AddSeconds(30);
        ForceCursor = true;
        Cursor = Cursors.Wait;
        _openWaitTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(60) };
        _openWaitTimer.Tick -= OpenWaitTick;
        _openWaitTimer.Tick += OpenWaitTick;
        _openWaitTimer.Start();
    }

    /// <summary>The first page of the opened file is now being shown; the cursor waits for its image.</summary>
    internal void MarkOpenShown() => _openWaitShown = true;

    internal void EndOpenWait()
    {
        _openWaitTimer?.Stop();
        ClearValue(CursorProperty);
        ClearValue(ForceCursorProperty);
        OpenWaitEnded?.Invoke();
    }

    /// <summary>A file open is waiting for its first page image (the start-up splash stays until it is there).</summary>
    internal bool OpenWaitActive => _openWaitTimer?.IsEnabled == true;

    /// <summary>The wait of <see cref="BeginOpenWait"/> is over: the first page is on screen, or the open gave up.</summary>
    internal event Action? OpenWaitEnded;

    private void OpenWaitTick(object? sender, EventArgs e)
    {
        if (DateTime.UtcNow > _openWaitDeadline || (_openWaitShown && _readerPage?.ReaderDisplayBitmap != null)) EndOpenWait();
    }
}
