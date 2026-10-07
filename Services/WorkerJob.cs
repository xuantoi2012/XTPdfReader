using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XTPdfMergeApp.Services;

/// <summary>
/// Windows Job Object với KILL_ON_JOB_CLOSE: các process worker (python.exe của MuPDF) được gán vào đây, nên khi app thoát bất thường
/// (crash, Task Manager "End task") Windows tự đóng handle job và giết hết worker — không còn python.exe mồ côi giữ RAM.
/// Thoát bình thường vẫn dừng worker như cũ (ProcessExit).
/// </summary>
internal static class WorkerJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;

    private static readonly IntPtr Job = Create();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong Read, Write, Other, ReadBytes, WriteBytes, OtherBytes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

    private static IntPtr Create()
    {
        if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
        try
        {
            IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;
            var limits = new ExtendedLimits { Basic = { LimitFlags = KillOnJobClose } };
            int size = Marshal.SizeOf<ExtendedLimits>();
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(limits, buffer, false);
                return SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)size) ? job : IntPtr.Zero;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>Gán <paramref name="process"/> vào job (best-effort). false = không gán được (vd job tạo lỗi); worker vẫn chạy bình thường.</summary>
    public static bool Assign(Process process)
    {
        if (Job == IntPtr.Zero) return false;
        try { return AssignProcessToJobObject(Job, process.Handle); }
        catch { return false; }
    }

    /// <summary>Process có đang nằm trong job này không (dùng cho test).</summary>
    public static bool Contains(Process process)
    {
        if (Job == IntPtr.Zero) return false;
        try { return IsProcessInJob(process.Handle, Job, out bool inJob) && inJob; }
        catch { return false; }
    }
}
