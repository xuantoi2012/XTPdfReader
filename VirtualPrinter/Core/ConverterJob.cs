using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace XTReader.Printing;

// The kernel closes this handle on service termination and kills its converters.
internal static class ConverterJob
{
    private static readonly SafeFileHandle Handle = Create();
    public static void Assign(Process process)
    {
        if (!AssignProcessToJobObject(Handle, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot protect converter lifetime");
    }
    private static SafeFileHandle Create()
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new ExtendedLimits { Basic = { LimitFlags = 0x2000 | 0x100 | 0x200 }, ProcessMemoryLimit = (UIntPtr)(256UL * 1024 * 1024), JobMemoryLimit = (UIntPtr)(384UL * 1024 * 1024) };
        if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
        return handle;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    { public long ProcessTime, JobTime; public uint LimitFlags; public UIntPtr MinWorkingSet, MaxWorkingSet; public uint ActiveProcesses; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    { public ulong Read, Write, Other, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
