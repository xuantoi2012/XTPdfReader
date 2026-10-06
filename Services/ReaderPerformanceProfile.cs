using System;
using System.Threading;

namespace XTPdfMergeApp.Services;

internal enum ReaderPerformanceMode { MemorySaving, Balance, Performance, Ultra, Maximum }
internal sealed record ReaderPerformanceProfile(ReaderPerformanceMode Mode, long CacheLimit, long SoftLimit,
    long UrgentLimit, long RasterLimit, int PrefetchPages, int NativeLists, long WorkerRecycleLimit,
    int BackgroundIdleSeconds, int IdleGcSeconds, bool RetainDistantImages)
{
    private const long MiB = 1024 * 1024;
    internal static ReaderPerformanceProfile For(ReaderPerformanceMode mode) => mode switch
    {
        ReaderPerformanceMode.Balance => new(mode, 1024 * MiB, 2048 * MiB, 3072 * MiB, 256 * MiB, 2, 8, 512 * MiB, 90, 20, true),
        ReaderPerformanceMode.Performance => new(mode, 1280 * MiB, 2560 * MiB, 3840 * MiB, 288 * MiB, 3, 9, 576 * MiB, 120, 28, true),
        ReaderPerformanceMode.Ultra => new(mode, 1536 * MiB, 3072 * MiB, 4608 * MiB, 320 * MiB, 3, 10, 640 * MiB, 150, 35, true),
        ReaderPerformanceMode.Maximum => new(mode, 2048 * MiB, 4096 * MiB, 6144 * MiB, 384 * MiB, 4, 12, 768 * MiB, 180, 45, true),
        ReaderPerformanceMode.MemorySaving => new(mode, 512 * MiB, 1024 * MiB, 1536 * MiB, 192 * MiB, 1, 4, 320 * MiB, 30, 8, false),
        _ => For(ReaderPerformanceMode.Balance)
    };
    private static ReaderPerformanceProfile _current = For(AppSettings.PerformanceMode);
    internal static ReaderPerformanceProfile Current => Volatile.Read(ref _current);
    internal static void Apply(ReaderPerformanceMode mode) => Volatile.Write(ref _current, For(mode));
}
