using System;

namespace XTPdfMergeApp.Services;

internal enum MemoryPressureState { Normal, Pressure, Critical }

// Values are bytes. Private commit is diagnostic/accounting data, not resident RAM.
internal readonly record struct SystemMemorySample(long TotalPhysical, long AvailablePhysical,
    int PhysicalLoadPercent, long CommitLimit, long CommitAvailable, long ParentPrivate, long WorkerPrivate)
{
    internal bool IsValid => TotalPhysical > 0 && AvailablePhysical >= 0 &&
        AvailablePhysical <= TotalPhysical && CommitLimit > 0 && CommitAvailable >= 0 && CommitAvailable <= CommitLimit;
}

internal readonly record struct AdaptiveMemoryDecision(MemoryPressureState State, long RetentionTarget, string Reason)
{
    internal int BackgroundLanes => State == MemoryPressureState.Normal ? 2 : State == MemoryPressureState.Pressure ? 1 : 0;
}

/// <summary>Pure policy; injectable samples and monotonic elapsed time allow pressure tests without filling RAM.</summary>
internal sealed class AdaptiveMemoryPolicy
{
    internal const long MiB = 1024 * 1024;
    internal const long Floor = 256 * MiB;
    internal const long NormalCacheLimit = 512 * MiB;
    internal const long AppSoftLimit = 1024 * MiB;
    internal const long AppUrgentLimit = 1536 * MiB;
    internal static readonly TimeSpan RecoveryDelay = TimeSpan.FromSeconds(45);
    private MemoryPressureState _state;
    private TimeSpan? _recoverySince;
    private ReaderPerformanceMode _mode;
    private long _target = NormalCacheLimit;
    internal AdaptiveMemoryDecision Current { get; private set; } = new(MemoryPressureState.Normal, NormalCacheLimit, "starting");

    internal AdaptiveMemoryDecision Update(SystemMemorySample sample, TimeSpan now, ReaderPerformanceProfile? profile = null)
    {
        // Failed probes must not be mistaken for a machine with zero spare memory.
        if (!sample.IsValid) return Current;
        profile ??= ReaderPerformanceProfile.For(ReaderPerformanceMode.MemorySaving);
        if (_mode != profile.Mode) { _mode = profile.Mode; _state = MemoryPressureState.Normal; _recoverySince = null; }
        long reserve = Math.Max(512 * MiB, sample.TotalPhysical / 10);
        long commitReserve = Math.Max(256 * MiB, sample.CommitLimit / 20);
        long appPrivate = sample.ParentPrivate + sample.WorkerPrivate;
        bool critical = appPrivate >= profile.UrgentLimit || sample.PhysicalLoadPercent >= 94 || sample.AvailablePhysical < reserve / 2 ||
            sample.CommitAvailable < commitReserve / 2;
        bool pressure = appPrivate >= profile.SoftLimit || sample.PhysicalLoadPercent >= 85 || sample.AvailablePhysical < reserve ||
            sample.CommitAvailable < commitReserve ||
            ((double)sample.ParentPrivate + sample.WorkerPrivate > sample.TotalPhysical * .3 && sample.AvailablePhysical < reserve * 1.5);
        var requested = critical ? MemoryPressureState.Critical : pressure ? MemoryPressureState.Pressure : MemoryPressureState.Normal;
        string reason = appPrivate >= profile.UrgentLimit ? "app private memory above urgent threshold" : appPrivate >= profile.SoftLimit ? "app private memory above soft threshold" :
            critical ? "physical/commit headroom critical" : pressure ? "physical/commit reserve low" : "memory available";
        if (requested > _state) { _state = requested; _recoverySince = null; }
        else if (requested < _state)
        {
            bool recovered = appPrivate < profile.SoftLimit * 7 / 8 && sample.PhysicalLoadPercent < 78 && sample.AvailablePhysical > reserve * 1.5 &&
                sample.CommitAvailable > commitReserve * 2;
            if (!recovered) _recoverySince = null;
            else
            {
                _recoverySince ??= now;
                if (now - _recoverySince.Value >= RecoveryDelay) { _state = requested; _recoverySince = null; }
            }
            reason = _state == requested ? "memory recovered" : "waiting for sustained recovery";
        }
        else _recoverySince = null;

        long desired = _state switch
        {
            MemoryPressureState.Critical => Floor,
            MemoryPressureState.Pressure => Math.Clamp(sample.TotalPhysical / 32, Floor, 384 * MiB),
            _ => Math.Clamp(Math.Min(sample.TotalPhysical / 5, Math.Max(0, sample.AvailablePhysical - reserve) / 2), Floor, profile.CacheLimit)
        };
        // Retention limits grow gradually; this allocates no memory and starts no worker.
        _target = desired < _target ? desired : Math.Min(desired, _target + 64 * MiB);
        return Current = new(_state, _target, reason);
    }
}
