using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using XTPdfMergeApp.Controls;

namespace XTPdfMergeApp.Services;

internal static class AdaptiveMemoryController
{
    private static readonly object Sync = new();
    private static readonly List<WeakReference<ContinuousPdfView>> Views = new();
    private static WeakReference<ContinuousPdfView>[] _registeredViews = Array.Empty<WeakReference<ContinuousPdfView>>();
    private static readonly AdaptiveMemoryPolicy Policy = new();
    private static Timer? _timer;
    private static CancellationTokenSource? _lifetime;
    private static int _running;
    private static int _state;
    private static long _target = AdaptiveMemoryPolicy.NormalCacheLimit;
    private static SystemMemorySample _sample;
    private static string _reason = "not started";
    private static long _trims;
    private static long _releasedBytes, _lastActivity, _lastCollection, _collections;
    internal static bool HasRegisteredViews => Volatile.Read(ref _registeredViews).Any(w => w.TryGetTarget(out _));
    internal static void NoteReleasedBytes(long bytes) { if (bytes > 0) Interlocked.Add(ref _releasedBytes, bytes); }
    internal static void NoteActivity() => Interlocked.Exchange(ref _lastActivity, Stopwatch.GetTimestamp());
    internal static MemoryPressureState State => (MemoryPressureState)Volatile.Read(ref _state);
    internal static bool AllowSpeculation => State == MemoryPressureState.Normal;
    internal static int BackgroundLanes => State == MemoryPressureState.Normal ? ExperimentalMuPdfViewport.MaxBackgroundLanes : State == MemoryPressureState.Pressure ? 1 : 0;

    internal static void Register(ContinuousPdfView view)
    {
        lock (Sync)
        {
            Views.RemoveAll(w => !w.TryGetTarget(out _)); Views.Add(new(view));
            Volatile.Write(ref _registeredViews, Views.ToArray());
        }
    }

    private static ContinuousPdfView[] LiveViews()
    {
        return Volatile.Read(ref _registeredViews).Select(w => w.TryGetTarget(out var view) ? view : null).OfType<ContinuousPdfView>().ToArray();
    }

    // Each view publishes an immutable page set from its own dispatcher; never read WPF properties here.
    internal static bool IsProtected(string path, int page)
    {
        foreach (var weak in Volatile.Read(ref _registeredViews))
            if (weak.TryGetTarget(out var view) && view.IsMemoryProtected(path, page)) return true;
        return false;
    }
    internal static bool IsProtectedImage(BitmapSource bitmap)
    {
        foreach (var weak in Volatile.Read(ref _registeredViews))
            if (weak.TryGetTarget(out var view) && view.IsMemoryProtectedImage(bitmap)) return true;
        return false;
    }
    internal static (string Path, int Page)[] ProtectedPages() => LiveViews().SelectMany(v => v.MemoryProtectedPages).Distinct().ToArray();
    internal static (string Path, int Page)[] NativeProtectedPages() => LiveViews().SelectMany(v => v.NativeProtectedPages).Distinct().ToArray();
    internal static bool IsNativeProtected(string path, int page)
        => NativeProtectedPages().Contains((path.ToUpperInvariant(), page));

    internal static void Start(Func<SystemMemorySample?>? sampleReader = null, TimeSpan? interval = null)
    {
        lock (Sync)
        {
            if (_timer != null) return;
            _lifetime = new();
            var token = _lifetime.Token;
            var cadence = interval ?? TimeSpan.FromSeconds(1);
            _timer = new Timer(_ => { _ = TickAsync(token, sampleReader); }, null, TimeSpan.Zero, cadence);
        }
    }

    internal static void Stop()
    {
        lock (Sync)
        {
            _timer?.Dispose(); _timer = null;
            _lifetime?.Cancel(); _lifetime?.Dispose(); _lifetime = null;
        }
    }

    private static async Task TickAsync(CancellationToken token, Func<SystemMemorySample?>? sampleReader)
    {
        if (token.IsCancellationRequested || Interlocked.Exchange(ref _running, 1) != 0) return;
        try
        {
            SystemMemorySample sample;
            if (sampleReader != null)
            {
                var injected = sampleReader();
                if (injected == null) return;
                sample = injected.Value;
            }
            else
            {
                if (!MemoryProbe.TrySampleSystemMemory(out sample)) return;
                using var parent = Process.GetCurrentProcess();
                sample = sample with { ParentPrivate = parent.PrivateMemorySize64,
                    WorkerPrivate = ExperimentalMuPdfViewport.WorkerPrivateBytes + ExperimentalMuPdfViewport.WorkerSharedMemoryBytes };
            }
            token.ThrowIfCancellationRequested();
            var profile = ReaderPerformanceProfile.Current;
            var decision = Policy.Update(sample, Stopwatch.GetElapsedTime(0), profile);
            await ApplyAsync(decision, token).ConfigureAwait(false);
            // Eviction releases references; WPF bitmap wrappers may still await finalization.
            // Rate-limited noncompacting background collection also handles sustained scrolling
            // above the app threshold, where waiting for user idle alone cannot arrest growth.
            bool idle = Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastActivity)).TotalSeconds >= 2;
            if ((idle || decision.State != MemoryPressureState.Normal) &&
                (Interlocked.Read(ref _releasedBytes) >= 64 * AdaptiveMemoryPolicy.MiB || sample.ParentPrivate + sample.WorkerPrivate >= profile.SoftLimit) &&
                Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastCollection)).TotalSeconds >= (decision.State == MemoryPressureState.Normal ? profile.IdleGcSeconds : 3))
            {
                token.ThrowIfCancellationRequested();
                Interlocked.Exchange(ref _lastCollection, Stopwatch.GetTimestamp());
                Interlocked.Exchange(ref _releasedBytes, 0);
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false, compacting: false);
                Interlocked.Increment(ref _collections);
            }
            lock (Sync) { _sample = sample; _reason = decision.Reason; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Debug.WriteLine($"Adaptive memory sample failed: {ex.Message}"); }
        finally { Volatile.Write(ref _running, 0); }
    }

    // Also used by offscreen integration tests with fake pressure decisions.
    internal static async Task ApplyAsync(AdaptiveMemoryDecision decision, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var previous = State;
        Volatile.Write(ref _state, (int)decision.State);
        Interlocked.Exchange(ref _target, decision.RetentionTarget);
        foreach (var view in LiveViews())
        {
            if (view.Dispatcher.HasShutdownStarted) continue;
            await view.Dispatcher.InvokeAsync(() => view.ApplyMemoryPressure(decision.State), DispatcherPriority.Background, token).Task.ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();
        long total = Math.Max(AdaptiveMemoryPolicy.Floor, decision.RetentionTarget);
        // Conservative ownership allocations: bridge and reader often refer to the same bitmap.
        // These limits are coordinated retention targets, never a process-memory hard cap.
        ReaderWindow.ApplyReaderMemoryBudget(total * 40 / 100);
        ContinuousPdfView.ApplyRegionMemoryBudget(total * 15 / 100);
        ThumbnailCache.ApplyMemoryBudget(total * 5 / 100);
        ExperimentalMuPdfViewport.ApplyMemoryBudget(total * 40 / 100);
        if (decision.State != MemoryPressureState.Normal || Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastActivity)).TotalSeconds >= 2)
        {
            Interlocked.Increment(ref _trims);
            await ExperimentalMuPdfViewport.TrimMemoryAsync(decision.State, token).ConfigureAwait(false);
        }
        if (previous != decision.State)
            DiagnosticsLog.Event($"MuPDF RAM: {previous} → {decision.State}, retention {total / AdaptiveMemoryPolicy.MiB} MiB ({decision.Reason})");
    }

    internal static string Describe()
    {
        lock (Sync)
            return $"{ReaderPerformanceProfile.Current.Mode}: {State}, target {Interlocked.Read(ref _target) / AdaptiveMemoryPolicy.MiB} MiB, background lanes {BackgroundLanes}, trims {Interlocked.Read(ref _trims)}, background collections {Interlocked.Read(ref _collections)}; " +
                $"available {_sample.AvailablePhysical / AdaptiveMemoryPolicy.MiB} MiB, commit headroom {_sample.CommitAvailable / AdaptiveMemoryPolicy.MiB} MiB; " +
                $"parent private/worker private + shared {_sample.ParentPrivate / AdaptiveMemoryPolicy.MiB}/{_sample.WorkerPrivate / AdaptiveMemoryPolicy.MiB} MiB; {_reason}";
    }
}
