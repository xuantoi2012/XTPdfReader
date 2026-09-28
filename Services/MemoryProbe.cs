using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace XTPdfMergeApp.Services;

/// <summary>
/// Quy bộ nhớ private của tiến trình về nguồn: vùng GC .NET đã cam kết, từng heap native (PDFium dùng heap CRT của
/// nó), tổng theo loại vùng và các vùng private lớn nhất. Chỉ để chẩn đoán (cửa sổ Debug / log), Windows.
/// </summary>
internal static class MemoryProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public UIntPtr RegionSize;
        public uint State, Protect, Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HeapSummaryInfo
    {
        public uint Size;
        public UIntPtr Allocated, Committed, Reserved, MaxReserve;
    }

    [DllImport("kernel32.dll")] private static extern UIntPtr VirtualQuery(IntPtr address, out MemoryBasicInformation info, UIntPtr length);
    [DllImport("kernel32.dll")] private static extern uint GetProcessHeaps(uint count, [Out] IntPtr[] heaps);
    [DllImport("kernel32.dll")] private static extern bool HeapSummary(IntPtr heap, uint flags, ref HeapSummaryInfo summary);

    [DllImport("kernel32.dll")] private static extern UIntPtr HeapCompact(IntPtr heap, uint flags);

    /// <summary>Trả về hệ điều hành phần đã cam kết nhưng không còn cấp phát của mọi heap native (PDFium giải phóng document
    /// nhưng heap CRT không tự decommit). Gọi sau khi đóng document; an toàn với mọi luồng.</summary>
    public static void CompactNativeHeaps()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var handles = new IntPtr[256];
            uint count = GetProcessHeaps((uint)handles.Length, handles);
            for (int i = 0; i < Math.Min(count, handles.Length); i++) HeapCompact(handles[i], 0);
        }
        catch { /* best effort */ }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    /// <summary>% RAM vật lý của máy đang dùng (0–100); 0 nếu không đọc được.</summary>
    public static int SystemMemoryLoadPercent()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref status) ? (int)status.MemoryLoad : 0;
        }
        catch { return 0; }
    }

    private const uint MemCommit = 0x1000, MemPrivate = 0x20000, MemMapped = 0x40000, MemImage = 0x1000000;
    private static double Mb(ulong bytes) => bytes / 1048576.0;

    public static string Describe()
    {
        if (!OperatingSystem.IsWindows()) return "  (chỉ Windows)";
        var sb = new StringBuilder();
        try
        {
            var gc = GC.GetGCMemoryInfo();
            sb.AppendLine($"  GC .NET: heap {Mb((ulong)gc.HeapSizeBytes):0} MB, đã cam kết {Mb((ulong)gc.TotalCommittedBytes):0} MB, phân mảnh {Mb((ulong)gc.FragmentedBytes):0} MB");

            ulong privateCommit = 0, mapped = 0, image = 0;
            var big = new List<(ulong Size, ulong Base)>();
            ulong address = 0;
            uint size = (uint)Marshal.SizeOf<MemoryBasicInformation>();
            while (address < 0x7FFFFFFF0000UL &&
                   VirtualQuery((IntPtr)(long)address, out var info, (UIntPtr)size) != UIntPtr.Zero)
            {
                ulong length = info.RegionSize.ToUInt64();
                if (info.State == MemCommit)
                {
                    if (info.Type == MemPrivate) { privateCommit += length; if (length >= 8UL << 20) big.Add((length, (ulong)(long)info.BaseAddress)); }
                    else if (info.Type == MemMapped) mapped += length;
                    else if (info.Type == MemImage) image += length;
                }
                address = (ulong)(long)info.BaseAddress + length;
            }
            sb.AppendLine($"  Vùng đã cam kết: private {Mb(privateCommit):0} MB, mapped {Mb(mapped):0} MB, image (DLL) {Mb(image):0} MB");

            var handles = new IntPtr[256];
            uint count = GetProcessHeaps((uint)handles.Length, handles);
            ulong heapCommitted = 0, heapAllocated = 0;
            var heapRows = new List<(ulong Committed, ulong Allocated, IntPtr Handle)>();
            for (int i = 0; i < Math.Min(count, handles.Length); i++)
            {
                var summary = new HeapSummaryInfo { Size = (uint)Marshal.SizeOf<HeapSummaryInfo>() };
                if (!HeapSummary(handles[i], 0, ref summary)) continue;
                heapCommitted += summary.Committed.ToUInt64();
                heapAllocated += summary.Allocated.ToUInt64();
                heapRows.Add((summary.Committed.ToUInt64(), summary.Allocated.ToUInt64(), handles[i]));
            }
            sb.AppendLine($"  Heap native: {count} heap, cam kết {Mb(heapCommitted):0} MB, đang cấp phát {Mb(heapAllocated):0} MB");
            heapRows.Sort((a, b) => b.Committed.CompareTo(a.Committed));
            foreach (var row in heapRows.GetRange(0, Math.Min(6, heapRows.Count)))
                if (row.Committed >= 4UL << 20)
                    sb.AppendLine($"    heap 0x{(long)row.Handle:X}: cam kết {Mb(row.Committed):0} MB, cấp phát {Mb(row.Allocated):0} MB");

            double other = Mb(privateCommit) - Mb((ulong)gc.TotalCommittedBytes) - Mb(heapCommitted);
            sb.AppendLine($"  Private − GC đã cam kết − heap native ≈ {other:0} MB (PartitionAlloc/VirtualAlloc trực tiếp, stack, WPF/GPU…)");
            big.Sort((a, b) => b.Size.CompareTo(a.Size));
            sb.Append("  Vùng private lớn nhất (MB): ");
            sb.AppendLine(big.Count == 0 ? "-" : string.Join(", ", big.GetRange(0, Math.Min(10, big.Count)).ConvertAll(b => $"{Mb(b.Size):0}")));
        }
        catch (Exception ex) { sb.AppendLine("  " + ex.Message); }
        return sb.ToString().TrimEnd();
    }
}
