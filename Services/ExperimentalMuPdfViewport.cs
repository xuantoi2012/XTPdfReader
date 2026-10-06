using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XTPdfMergeApp.Services;

// Native contexts stay in independent processes; page affinity retains display lists.
internal static class ExperimentalMuPdfViewport
{
    private static readonly IReadOnlyDictionary<string, string> LocalSettings = LoadLocalSettings();
    private sealed class WorkerSlot
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal readonly PdfRenderGate Scheduling = new();
        internal Process? Worker;
        internal long LastUse;
        internal long LastMemoryTrim;
        internal int Pending;
        internal string? LastPath;
        internal int LastPage;
    }
    private static readonly WorkerSlot[] Workers = { new(), new(), new(), new() };
    private readonly record struct RasterKey(string Path, long Modified, long Length, string Layers, bool Annotations, bool Alpha, int Page, int Width, int Height, Int32Rect Rect);
    private static readonly object CacheLock = new();
    private static long _rasterGeneration;
    internal static void InvalidateRasterCache(Func<string, int, bool> match)
    {
        lock (CacheLock)
        {
            _rasterGeneration++;
            Cache.RemoveWhere(key => match(key.Path, key.Page + 1));
        }
    }
    private static readonly BitmapMemoryCache<RasterKey> Cache = new((BalancedMode ? 192L : 2048L) * 1024 * 1024);
    private static readonly RenderMemoryBudget RasterBudget = new(192 * AdaptiveMemoryPolicy.MiB);
    private static readonly ConcurrentDictionary<(string Path, int Page), double> PageAspects = new();
    internal static (long Used, long Peak) RasterMemoryStats => RasterBudget.Stats;
    private static long _recycledWorkers;
    internal static long RecycledWorkerCount => Interlocked.Read(ref _recycledWorkers);
#if MUPDF_ONLY
    internal static bool BalancedMode => true;
    internal static bool ThroughputMode => true;
#else
    internal static bool BalancedMode => Setting("XTPDF_MUPDF_BALANCED") == "1";
    internal static bool ThroughputMode => Setting("XTPDF_MUPDF_THROUGHPUT") == "1";
#endif
    internal static int RunningWorkerCount
    {
        get
        {
            int count = 0;
            foreach (var slot in Workers)
            {
                try { if (slot.Worker is { HasExited: false }) count++; }
                catch (InvalidOperationException) { }
            }
            return count;
        }
    }
    internal static (int Count, long Bytes) CacheStats
    {
        get { lock (CacheLock) return (Cache.Count, Cache.Bytes); }
    }
    internal static int RunningBackgroundWorkerCount
    {
        get
        {
            int count = 0;
            for (int index = 2; index < Workers.Length; index++)
                try { if (Workers[index].Worker is { HasExited: false }) count++; }
                catch (InvalidOperationException) { }
            return count;
        }
    }
    internal static long CurrentCacheBudget { get { lock (CacheLock) return Cache.BudgetBytes; } }
    internal static void ApplyMemoryBudget(long bytes)
    {
        lock (CacheLock)
        {
            Cache.KeepImage = AdaptiveMemoryController.IsProtectedImage;
            if (AdaptiveMemoryController.HasRegisteredViews && (!ReaderPerformanceProfile.Current.RetainDistantImages || !AdaptiveMemoryController.AllowSpeculation))
                Cache.RemoveWhere(key => !AdaptiveMemoryController.IsProtected(key.Path, key.Page + 1));
            Cache.SetBudget(bytes);
        }
    }
    private static volatile bool _failed;
    private static readonly Timer IdleTimer = new(_ => TrimIdle(), null, 10000, 10000);
    private static string? Setting(string name) => Environment.GetEnvironmentVariable(name) ??
        (LocalSettings.TryGetValue(name, out var value) ? value : DefaultSetting(name));
    private static string? DefaultSetting(string name)
    {
#if MUPDF_ONLY
        return name switch
        {
            "XTPDF_EXPERIMENTAL_ENGINE" => "mupdf",
            "XTPDF_MUPDF_ALL_DOCUMENTS" or "XTPDF_MUPDF_ALL_PAGES" => "1",
            "XTPDF_MUPDF_WORKER" => Path.Combine(AppContext.BaseDirectory, "MuPdfWorker.py"),
            "XTPDF_MUPDF_PYTHON" => Path.Combine(AppContext.BaseDirectory, "MuPdfRuntime", "python.exe"),
            "XTPDF_MUPDF_PACKAGES" => Path.Combine(AppContext.BaseDirectory, "MuPdfRuntime", "Lib", "site-packages"),
            _ => null
        };
#else
        return null;
#endif
    }

    private static IReadOnlyDictionary<string, string> LoadLocalSettings()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "mupdf-trial.json");
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new()
                : new Dictionary<string, string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"[MuPDF viewport] Ignoring invalid local trial configuration: {ex.Message}");
            return new Dictionary<string, string>();
        }
    }
    internal static double WorkerPrivateMiB => WorkerPrivateBytes / 1048576d;
    internal static long WorkerPrivateBytes
    {
        get
        {
            long total = 0;
            foreach (var slot in Workers)
            {
                var worker = slot.Worker;
                if (worker == null) continue;
                try { worker.Refresh(); total += worker.PrivateMemorySize64; }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
            return total;
        }
    }
#if MUPDF_ONLY
    internal static bool CanRender(string path, int page, string layers) => !_failed && page >= 0;
#else
    internal static bool CanRender(string path, int page, string layers) =>
        !_failed && page >= 0 && (page == 0 || Setting("XTPDF_MUPDF_ALL_PAGES") == "1") && (layers.Length == 0 || BalancedMode) &&
        (string.Equals(Setting("XTPDF_EXPERIMENTAL_ENGINE"), "mupdf", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(Setting("XTPDF_EXPERIMENTAL_ENGINE"), "mupdf-native", StringComparison.OrdinalIgnoreCase)) &&
        (Setting("XTPDF_MUPDF_ALL_DOCUMENTS") == "1" ||
         string.Equals(Path.GetFullPath(path), Setting("XTPDF_MUPDF_SOURCE"), StringComparison.OrdinalIgnoreCase) ||
         string.Equals(Path.GetFullPath(path), Setting("XTPDF_MUPDF_SOURCE_SECOND"), StringComparison.OrdinalIgnoreCase));
#endif

    internal static async Task<BitmapSource?> RenderFullPageAsync(string path, int page, double maxWidth,
        CancellationToken token, PdfRenderPriority priority = PdfRenderPriority.Visible, bool withAnnotations = false)
    {
        int width = Math.Clamp((int)Math.Ceiling(maxWidth), 16, ThroughputMode ? 8192 : 4096);
        // One reusable sharp full-page image covers fit-to-page and nearby zooms.
        // Tiny sidebar previews must not allocate a high-resolution page.
        if (ThroughputMode && width >= 1024) width = Math.Max(width, 4608);
        var result = await RenderAsync(path, page, width, 0, new[] { new Int32Rect(0, 0, width, 0) }, token, priority, withAnnotations).ConfigureAwait(false);
        return result[0];
    }
    internal static bool CanRenderFullPage(string path, int page, string layers) =>
        (BalancedMode || string.Equals(Setting("XTPDF_EXPERIMENTAL_ENGINE"), "mupdf", StringComparison.OrdinalIgnoreCase)) &&
        CanRender(path, page, layers);

    static ExperimentalMuPdfViewport()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { foreach (var slot in Workers) Stop(slot); };
    }

    internal static async Task<List<BitmapSource?>> RenderAsync(string path, int page,
        int fullWidth, int fullHeight, IReadOnlyList<Int32Rect> rectangles, CancellationToken token,
        PdfRenderPriority priority = PdfRenderPriority.Visible, bool withAnnotations = false, bool alpha = false)
    {
        token.ThrowIfCancellationRequested();
        if (PdfThumbnailService.IsDocumentSuspended(path))
            return new List<BitmapSource?>(new BitmapSource?[rectangles.Count]);
        // Network-drive stat calls must not occupy the WPF frame thread.
        var identity = await Task.Run(() =>
        {
            var file = new FileInfo(path);
            return (Path: file.FullName.ToUpperInvariant(), Modified: file.Exists ? file.LastWriteTimeUtc.Ticks : 0,
                Length: file.Exists ? file.Length : 0);
        }, token).ConfigureAwait(false);
        string normalized = identity.Path;
        long modified = identity.Modified;
        long fileLength = identity.Length;
        long rasterGeneration;
        lock (CacheLock) rasterGeneration = _rasterGeneration;
        string layers = PdfLayerStateStore.GetToken(path);
        RasterKey Key(Int32Rect rect) => new(normalized, modified, fileLength, layers, withAnnotations, alpha, page, fullWidth, fullHeight, rect);
        if (ThroughputMode)
        {
            var cached = new List<BitmapSource?>();
            lock (CacheLock)
                foreach (var rect in rectangles)
                {
                    if (!Cache.TryGetValue(Key(rect), out var image)) break;
                    cached.Add(image);
                }
            if (cached.Count == rectangles.Count) return cached;
        }
        // Reserve two independent processes for visible work. Speculation cannot
        // occupy them; two background lanes can prepare separate pages concurrently.
        int parity = priority == PdfRenderPriority.Visible || AdaptiveMemoryController.BackgroundLanes == 2 ? page & 1 : 0;
        var slot = Workers[ThroughputMode ? (priority == PdfRenderPriority.Visible ? 0 : 2) + parity : 0];
        await EnterSlotAsync(slot, priority, token).ConfigureAwait(false);
        bool replyDrained = true;
        try
        {
            token.ThrowIfCancellationRequested();
            if (PdfThumbnailService.IsDocumentSuspended(path))
                return new List<BitmapSource?>(new BitmapSource?[rectangles.Count]);
            var results = new List<BitmapSource?>();
            foreach (var rect in rectangles)
            {
                token.ThrowIfCancellationRequested();
                if (ThroughputMode)
                {
                    lock (CacheLock)
                    {
                        Cache.RemoveWhere(k => k.Path == normalized && (k.Modified != modified || k.Length != fileLength));
                        if (Cache.TryGetValue(Key(rect), out var cached)) { results.Add(cached); continue; }
                    }
                }
                double aspect = PageAspects.TryGetValue((normalized, page), out var ratio) ? ratio : 1.5;
                long heightEstimate = fullHeight == 0 ? Math.Max(1, (long)Math.Ceiling(fullWidth * aspect)) : rect.Height;
                long widthEstimate = fullHeight == 0 ? fullWidth : rect.Width;
                RasterBudget.SetCapacity(AdaptiveMemoryController.State == MemoryPressureState.Normal ? ReaderPerformanceProfile.Current.RasterLimit : 192 * AdaptiveMemoryPolicy.MiB);
                using var reservation = await RasterBudget.AcquireAsync(checked(widthEstimate * heightEstimate * (alpha ? 4 : 3) * 4), priority, token).ConfigureAwait(false);
                RecycleColdWorker(slot, normalized, page);
                var _worker = StartWorker(slot);
                slot.LastPath = normalized; slot.LastPage = page;
                var watch = Stopwatch.StartNew();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                string request = JsonSerializer.Serialize(new
                {
                    path,
                    page,
                    fullWidth,
                    fullHeight,
                    annotations = withAnnotations,
                    alpha,
                    password = PdfThumbnailService.TryGetDocumentPassword(path),
                    memoryState = (int)AdaptiveMemoryController.State,
                    nativeListLimit = ReaderPerformanceProfile.Current.NativeLists,
                    hidden = PdfLayerStateStore.GetHiddenOverride(path, out _),
                    rect = new[] { rect.X, rect.Y, rect.Width, rect.Height }
                });
                replyDrained = false;
                await _worker.StandardInput.WriteLineAsync(request.AsMemory(), timeout.Token).ConfigureAwait(false);
                await _worker.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
                var stream = _worker.StandardOutput.BaseStream;
                string header = await ReadHeaderAsync(stream, timeout.Token).ConfigureAwait(false);
                using var json = JsonDocument.Parse(header);
                var root = json.RootElement;
                if (root.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetString());
                int width = root.GetProperty("width").GetInt32(), height = root.GetProperty("height").GetInt32();
                int stride = root.GetProperty("stride").GetInt32(), length = root.GetProperty("length").GetInt32();
                if (width != rect.Width || (fullHeight != 0 && height != rect.Height) || height <= 0 || stride != checked(width * (alpha ? 4 : 3)) ||
                    length != checked(stride * height) || length > (ThroughputMode ? 512 : 128) * 1024 * 1024)
                    throw new InvalidDataException("Unexpected worker bitmap dimensions");
                byte[] pixels = new byte[length];
                await stream.ReadExactlyAsync(pixels, timeout.Token).ConfigureAwait(false);
                replyDrained = true;
                token.ThrowIfCancellationRequested();
                if (PdfThumbnailService.IsDocumentSuspended(path) || layers != PdfLayerStateStore.GetToken(path))
                    return new List<BitmapSource?>(new BitmapSource?[rectangles.Count]);
                var bitmap = BitmapSource.Create(width, height, 96, 96, alpha ? PixelFormats.Pbgra32 : PixelFormats.Rgb24, null, pixels, stride);
                bitmap.Freeze();
                RenderDiagnostics.RasterSlice.AddMilliseconds(root.GetProperty("renderMs").GetDouble());
                if (ThroughputMode) lock (CacheLock)
                    if (rasterGeneration == _rasterGeneration) Cache.Set(Key(rect), bitmap);
                results.Add(bitmap);
                Debug.WriteLine($"[MuPDF viewport] total={watch.Elapsed.TotalMilliseconds:F1}ms prepare={root.GetProperty("prepareMs").GetDouble():F1}ms render={root.GetProperty("renderMs").GetDouble():F1}ms");
            }
            return results;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (!replyDrained) Stop(slot);
            throw;
        }
        catch { if (!ThroughputMode) _failed = true; Stop(slot); throw; }
        finally { LeaveSlot(slot); }
    }

    private static Process StartWorker(WorkerSlot slot)
    {
        if (slot.Worker is { HasExited: false } existing) return existing;
        Stop(slot);
        bool native = !BalancedMode && string.Equals(Setting("XTPDF_EXPERIMENTAL_ENGINE"), "mupdf-native", StringComparison.OrdinalIgnoreCase);
        var info = new ProcessStartInfo(native
            ? Setting("XTPDF_MUPDF_NATIVE_WORKER") ?? throw new InvalidOperationException("Missing native worker path")
            : Setting("XTPDF_MUPDF_PYTHON") ?? throw new InvalidOperationException("Missing Python path"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (native) info.ArgumentList.Add("2");
        else
        {
            info.ArgumentList.Add("-u");
            info.ArgumentList.Add(Setting("XTPDF_MUPDF_WORKER") ?? throw new InvalidOperationException("Missing worker path"));
        }
        if (!native && Setting("XTPDF_MUPDF_PACKAGES") is { } packages)
            info.Environment["PYTHONPATH"] = packages;
        if (BalancedMode)
        {
            info.Environment["XTPDF_MUPDF_NO_RASTER_CACHE"] = "1";
            info.Environment["XTPDF_MUPDF_LISTS"] = "4";
            info.Environment["XTPDF_MUPDF_DOCUMENTS"] = "2";
        }
        var _worker = Process.Start(info) ?? throw new InvalidOperationException("Worker did not start");
        slot.Worker = _worker;
        _worker.ErrorDataReceived += (_, e) => { if (e.Data != null) Debug.WriteLine("[MuPDF worker] " + e.Data); };
        _worker.BeginErrorReadLine();
        return _worker;
    }

    internal static async Task<JsonElement> CommandAsync(string path, string op, int page = 0,
        object? data = null, CancellationToken token = default, int? workerIndex = null)
    {
        var slot = Workers[workerIndex ?? (ThroughputMode ? 2 + (AdaptiveMemoryController.BackgroundLanes == 2 ? page & 1 : 0) : 0)];
        await EnterSlotAsync(slot, PdfRenderPriority.Background, token).ConfigureAwait(false);
        bool replyDrained = true;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(path) && PdfThumbnailService.IsDocumentSuspended(path))
                throw new OperationCanceledException("Document is suspended");
            var worker = StartWorker(slot);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var request = JsonSerializer.Serialize(new
            {
                path,
                op,
                page,
                data,
                memoryState = (int)AdaptiveMemoryController.State,
                password = string.IsNullOrEmpty(path) ? null : PdfThumbnailService.TryGetDocumentPassword(path)
            });
            replyDrained = false;
            await worker.StandardInput.WriteLineAsync(request.AsMemory(), timeout.Token).ConfigureAwait(false);
            await worker.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
            // Drain the JSON reply before honoring caller cancellation.
            using var reply = JsonDocument.Parse(await ReadHeaderAsync(worker.StandardOutput.BaseStream, timeout.Token, 4 * 1024 * 1024).ConfigureAwait(false));
            replyDrained = true;
            token.ThrowIfCancellationRequested();
            if (reply.RootElement.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetString());
            if (op == "metadata" && reply.RootElement.TryGetProperty("sizes", out var sizes))
            {
                string normalized = Path.GetFullPath(path).ToUpperInvariant(); int index = 0;
                foreach (var size in sizes.EnumerateArray()) PageAspects[(normalized, index++)] = size[1].GetDouble() / size[0].GetDouble();
            }
            return reply.RootElement.Clone();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (!replyDrained) Stop(slot);
            throw;
        }
        catch (OperationCanceledException) when (replyDrained) { throw; }
        catch { Stop(slot); throw; }
        finally { LeaveSlot(slot); }
    }

    private static async Task EnterSlotAsync(WorkerSlot slot, PdfRenderPriority priority, CancellationToken token)
    {
        Interlocked.Increment(ref slot.Pending);
        try
        {
            await slot.Scheduling.WaitAsync(priority, token).ConfigureAwait(false);
            try { await slot.Gate.WaitAsync(token).ConfigureAwait(false); }
            catch { slot.Scheduling.Release(); throw; }
        }
        catch { Interlocked.Decrement(ref slot.Pending); throw; }
    }

    private static void LeaveSlot(WorkerSlot slot)
    {
        slot.LastUse = Stopwatch.GetTimestamp();
        slot.Gate.Release(); slot.Scheduling.Release();
        Interlocked.Decrement(ref slot.Pending);
    }

    internal static async Task RetireAsync(string path)
    {
        string normalized = Path.GetFullPath(path).ToUpperInvariant();
        foreach (var slot in Workers)
        {
            await slot.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (slot.Worker is not { HasExited: false } worker) continue;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { path, op = "close" }).AsMemory(), timeout.Token).ConfigureAwait(false);
                await worker.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
                using var reply = JsonDocument.Parse(await ReadHeaderAsync(worker.StandardOutput.BaseStream, timeout.Token).ConfigureAwait(false));
                if (!reply.RootElement.TryGetProperty("ok", out _)) Stop(slot);
            }
            catch { Stop(slot); throw; }
            finally { slot.Gate.Release(); }
        }
        lock (CacheLock) Cache.RemoveWhere(k => k.Path == normalized);
        foreach (var key in PageAspects.Keys.Where(k => k.Path == normalized)) PageAspects.TryRemove(key, out _);
    }

    internal static async Task ReleaseUnusedAsync(IReadOnlySet<string> active)
    {
        lock (CacheLock) Cache.RemoveWhere(k => !active.Contains(k.Path));
        foreach (var key in PageAspects.Keys.Where(k => !active.Contains(k.Path))) PageAspects.TryRemove(key, out _);
        foreach (var slot in Workers)
        {
            await slot.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (slot.Worker is not { HasExited: false } worker) continue;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { path = "", op = "release", data = active }).AsMemory(), timeout.Token).ConfigureAwait(false);
                await worker.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
                using var reply = JsonDocument.Parse(await ReadHeaderAsync(worker.StandardOutput.BaseStream, timeout.Token).ConfigureAwait(false));
                if (!reply.RootElement.TryGetProperty("ok", out _)) Stop(slot);
            }
            catch { Stop(slot); }
            finally { slot.Gate.Release(); }
        }
    }

    private static async Task<string> ReadHeaderAsync(Stream stream, CancellationToken token, int limit = 8192)
    {
        var bytes = new List<byte>();
        byte[] one = new byte[1];
        while (bytes.Count < limit)
        {
            await stream.ReadExactlyAsync(one, token).ConfigureAwait(false);
            if (one[0] == 10) return Encoding.UTF8.GetString(bytes.ToArray());
            bytes.Add(one[0]);
        }
        throw new InvalidDataException("Worker header exceeds limit");
    }

    private static void TrimIdle()
    {
        for (int index = 0; index < Workers.Length; index++)
        {
            var slot = Workers[index];
            if (Volatile.Read(ref slot.Pending) != 0 || !slot.Gate.Wait(0)) continue;
            try { if (Volatile.Read(ref slot.Pending) == 0 && Stopwatch.GetElapsedTime(slot.LastUse).TotalSeconds > (BalancedMode && index >= 2 ? 120 : ThroughputMode ? 1800 : 60)) Stop(slot); }
            finally { slot.Gate.Release(); }
        }
    }

    // Never starts a worker or interrupts an active request. All IPC replies are drained under its gate.
    internal static async Task TrimMemoryAsync(MemoryPressureState pressure, CancellationToken token = default)
    {
        if (!BalancedMode) return;
        for (int index = 0; index < Workers.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var slot = Workers[index];
            if (Volatile.Read(ref slot.Pending) != 0 || !slot.Gate.Wait(0)) continue;
            try
            {
                if (Volatile.Read(ref slot.Pending) != 0 || slot.Worker is not { HasExited: false } worker || Stopwatch.GetElapsedTime(slot.LastUse).TotalSeconds < 2) continue;
                if (index >= 2 && Stopwatch.GetElapsedTime(slot.LastUse).TotalSeconds >= (pressure == MemoryPressureState.Normal ? ReaderPerformanceProfile.Current.BackgroundIdleSeconds : 10))
                { Stop(slot); continue; }
                if (Stopwatch.GetElapsedTime(slot.LastMemoryTrim).TotalSeconds < 10) continue;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var pages = AdaptiveMemoryController.NativeProtectedPages().Select(p => new { path = p.Path, page = p.Page - 1 }).ToArray();
                await worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
                    { path = "", op = "memory", memoryState = (int)pressure, data = pages }).AsMemory(), timeout.Token).ConfigureAwait(false);
                await worker.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
                using var reply = JsonDocument.Parse(await ReadHeaderAsync(worker.StandardOutput.BaseStream, timeout.Token).ConfigureAwait(false));
                if (!reply.RootElement.TryGetProperty("ok", out _)) Stop(slot);
                slot.LastMemoryTrim = Stopwatch.GetTimestamp();
            }
            catch (Exception ex) { Debug.WriteLine($"MuPDF memory trim failed: {ex.Message}"); Stop(slot); }
            finally { slot.Gate.Release(); }
        }
    }

    private static void Stop(WorkerSlot slot)
    {
        var worker = Interlocked.Exchange(ref slot.Worker, null);
        if (worker == null) return;
        try { if (!worker.HasExited) worker.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        finally { worker.Dispose(); }
    }

    // Lane gate is held: recycle only an oversized context no longer backing the viewport.
    private static void RecycleColdWorker(WorkerSlot slot, string path, int page)
    {
        if (slot.Worker is not { HasExited: false } worker || slot.LastPath == null ||
            (slot.LastPath == path && slot.LastPage == page) ||
            AdaptiveMemoryController.IsNativeProtected(slot.LastPath, slot.LastPage + 1)) return;
        try
        {
            worker.Refresh();
            if (worker.PrivateMemorySize64 < (AdaptiveMemoryController.State == MemoryPressureState.Normal ? ReaderPerformanceProfile.Current.WorkerRecycleLimit : 320 * AdaptiveMemoryPolicy.MiB)) return;
            Stop(slot); Interlocked.Increment(ref _recycledWorkers);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    // Experiment/idle-policy primitive: keeps displayed bitmaps and never waits for a busy IPC lane.
    internal static int RetireIdleWorkers(int retainedForeground, double minimumIdleSeconds)
    {
        if (retainedForeground < -1 || retainedForeground > 1) throw new ArgumentOutOfRangeException(nameof(retainedForeground));
        int retired = 0;
        for (int index = 0; index < Workers.Length; index++)
        {
            if (index == retainedForeground) continue;
            var slot = Workers[index];
            if (Volatile.Read(ref slot.Pending) != 0 || !slot.Gate.Wait(0)) continue;
            try
            {
                if (Volatile.Read(ref slot.Pending) != 0 || slot.Worker == null || Stopwatch.GetElapsedTime(slot.LastUse).TotalSeconds < minimumIdleSeconds) continue;
                Stop(slot); retired++;
            }
            finally { slot.Gate.Release(); }
        }
        return retired;
    }

    internal static void Shutdown()
    {
        foreach (var slot in Workers)
        {
            slot.Gate.Wait();
            try { Stop(slot); }
            finally { slot.Gate.Release(); }
        }
        lock (CacheLock) Cache.Clear();
    }
}
