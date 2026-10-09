using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace XTPdfMergeApp.Services
{
    /// <summary>
    /// Bước #0 tối ưu hiệu năng (phương án C): PDFium không đọc file trên ổ mạng trực tiếp mà đọc qua
    /// <see cref="PdfBlockCache"/> — bộ đệm cả file chia khối 256 KB, nạp dần bởi 1 luồng đọc nền. Lệnh đọc
    /// của trang đang xem được chen lên trước đọc nền.
    ///
    /// Vì sao không đọc hết file rồi FPDF_LoadMemDocument64 (thiết kế #0 đầu tiên): trong ~8 s đọc nền, lệnh
    /// đọc nhỏ của PDFium phải xếp hàng sau khối đọc nền trên cùng đường truyền. Đo bằng
    /// <c>PdfBench window</c> trên file thật 163 MB ở ổ P: (~21 MB/s, đã chạm trần băng thông, đọc song song
    /// không nhanh hơn): parse trang mới TB 1041–1154 ms khi không đọc nền, 1637–1739 ms (max 11–12 s) khi
    /// đọc nền kiểu cũ, 116–127 ms với bộ đệm khối.
    ///
    /// Giới hạn: file &gt; <see cref="MaxFileBytes"/> hoặc vượt tổng ngân sách <see cref="TotalBudgetBytes"/>
    /// thì không đệm — PDFium mở file như cũ (FPDF_LoadDocument), tránh ăn hết RAM trên máy yếu.
    /// </summary>
    public static class PdfFileBuffer
    {
        /// <summary>File lớn hơn mức này không đệm vào RAM.</summary>
        public const long MaxFileBytes = 2L * 1024 * 1024 * 1024;

        /// <summary>Tổng dung lượng file tạm tối đa cho mọi file đang đệm: 1/3 chỗ trống của ổ chứa thư mục tạm, 2–16 GB (bộ đệm nằm trên đĩa cục bộ, không chiếm RAM riêng).</summary>
        public static readonly long TotalBudgetBytes = ComputeDiskBudget();

        private static long ComputeDiskBudget()
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetTempPath())!);
                return Math.Clamp(drive.AvailableFreeSpace / 3, 2L * 1024 * 1024 * 1024, 16L * 1024 * 1024 * 1024);
            }
            catch { return 4L * 1024 * 1024 * 1024; }
        }

        private static readonly bool DirectLocalDisks = Environment.GetEnvironmentVariable("XTPDF_DIRECT_LOCAL") == "1";
        private static readonly ConcurrentDictionary<string, bool> _localRoots = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Chỉ true khi đặt XTPDF_DIRECT_LOCAL=1 VÀ file nằm trên ổ cố định cục bộ: PDFium đọc thẳng, không đệm. Mặc định
        /// vẫn đệm cả file cục bộ — đo 28/09: cuộn chuột 40 nấc trên file 165 MB ở SSD mất 0,8–1,0 s khi đọc thẳng so với
        /// 0,05 s khi qua bộ đệm (đọc tuần tự 1 lần vào file tạm thay cho hàng nghìn lần đọc ngẫu nhiên nhỏ của PDFium).</summary>
        public static bool IsLocalDisk(string normalizedPath)
        {
            if (!DirectLocalDisks) return false;
            try
            {
                if (normalizedPath.StartsWith(@"\\", StringComparison.Ordinal)) return false;
                string? root = Path.GetPathRoot(normalizedPath);
                if (string.IsNullOrEmpty(root)) return false;
                return _localRoots.GetOrAdd(root, r => new DriveInfo(r).DriveType == DriveType.Fixed);
            }
            catch { return false; }
        }

        /// <summary>Ổ cố định cục bộ: không chép nền cả file sang file tạm, chỉ nạp khối PDFium thật sự đọc (cộng vài khối đọc trước).
        /// Đo 02/10 trên file 165 MB: Foxit chỉ đọc 6 MB lúc mở, bản chép nền đọc 176 MB (và ghi 165 MB file tạm).
        /// XTPDF_LAZY_LOCAL=0 trả về hành vi chép nền như cũ để so sánh.</summary>
        internal static readonly bool LazyLocal = Environment.GetEnvironmentVariable("XTPDF_LAZY_LOCAL") != "0";

        private static bool IsFixedLocalDrive(string normalizedPath)
        {
            try
            {
                if (normalizedPath.StartsWith(@"\\", StringComparison.Ordinal)) return false;
                string? root = Path.GetPathRoot(normalizedPath);
                if (string.IsNullOrEmpty(root)) return false;
                return _localRoots.GetOrAdd(root, r => new DriveInfo(r).DriveType == DriveType.Fixed);
            }
            catch { return false; }
        }

        private static readonly ConcurrentDictionary<string, PdfBlockCache> _entries = new(StringComparer.OrdinalIgnoreCase);
        private static long _reservedBytes;

        public enum InvalidateReason { Changed, Closed, Edited }
        private static long _created, _invalidatedChanged, _invalidatedClosed, _invalidatedEdited;

        /// <summary>Cho cửa sổ Debug: số lần tạo bộ đệm và số lần bỏ theo lý do (file đổi trên đĩa / file đóng / app ghi file).</summary>
        public static (long Created, long Changed, long Closed, long Edited) Counters => (Interlocked.Read(ref _created),
            Interlocked.Read(ref _invalidatedChanged), Interlocked.Read(ref _invalidatedClosed), Interlocked.Read(ref _invalidatedEdited));

        /// <summary>Cho cửa sổ Debug: các file đang đệm.</summary>
        public static IReadOnlyList<(string Path, long Length, double Loaded)> Snapshot()
            => _entries.Select(e => (e.Key, e.Value.Length, e.Value.LoadedFraction)).ToList();

        public static long ReservedBytes => Interlocked.Read(ref _reservedBytes);

        /// <summary>Bộ đệm của file (đường dẫn đã chuẩn hoá), đã tăng số tham chiếu — người gọi phải
        /// <see cref="PdfBlockCache.Release"/> khi xong (với PDFium: SAU FPDF_CloseDocument). Chưa có thì tạo và
        /// bắt đầu đọc nền. null = không đệm được (file quá lớn, hết ngân sách, không đọc được) → đọc file như cũ.</summary>
        public static PdfBlockCache? Acquire(string normalizedPath)
        {
            if (IsLocalDisk(normalizedPath)) return null; // đọc thẳng từ đĩa cục bộ
            FileInfo info;
            try
            {
                info = new FileInfo(normalizedPath);
                if (!info.Exists) return null;
            }
            catch
            {
                return null;
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (_entries.TryGetValue(normalizedPath, out var existing))
                {
                    if (existing.Length == info.Length && existing.LastWriteUtc == info.LastWriteTimeUtc)
                    {
                        if (existing.TryAddRef()) return existing;
                        continue; // vừa bị bỏ giữa chừng — thử lại
                    }
                    Invalidate(normalizedPath, InvalidateReason.Changed); // file đổi trên ổ mạng (người khác lưu đè) → đọc lại
                }

                long length = info.Length;
                if (length <= 0 || length > MaxFileBytes) return null;
                if (Interlocked.Add(ref _reservedBytes, length) > TotalBudgetBytes)
                {
                    Interlocked.Add(ref _reservedBytes, -length);
                    return null;
                }

                PdfBlockCache created;
                try
                {
                    created = new PdfBlockCache(normalizedPath, length, info.LastWriteTimeUtc,
                        onFreed: () => Interlocked.Add(ref _reservedBytes, -length));
                }
                catch
                {
                    Interlocked.Add(ref _reservedBytes, -length);
                    return null;
                }

                if (!_entries.TryAdd(normalizedPath, created))
                {
                    created.Release(); // luồng khác vừa tạo trước — dùng bản đó
                    continue;
                }
                Interlocked.Increment(ref _created);
                if (LazyLocal && IsFixedLocalDrive(normalizedPath)) created.SetLazy();
                else created.StartBackgroundRead();
                if (created.TryAddRef()) return created; // 1 tham chiếu của registry + 1 của người gọi
                // (vừa bị Invalidate ngay sau khi thêm — thử lại từ đầu)
            }
            return null;
        }

        /// <summary>Phần file đã nạp vào RAM (0..1), null = file không có bộ đệm (chưa mở / quá lớn / hết ngân sách).</summary>
        public static double? GetLoadedFraction(string normalizedPath)
            => _entries.TryGetValue(normalizedPath, out var entry) ? entry.LoadedFraction : RemoteFileStage.Fraction(normalizedPath);

        /// <summary>File đang có bộ đệm khối (mọi bản PDFium đọc chung, file chỉ qua mạng 1 lần).</summary>
        public static bool IsBuffered(string normalizedPath) => _entries.ContainsKey(normalizedPath);

        /// <summary>File sắp bị ghi đè (xoay trang, annotation) hoặc đã đổi — bỏ bộ đệm cũ. Document PDFium
        /// đang dùng nó vẫn đọc được tới khi đóng (bộ nhớ chỉ trả khi mọi tham chiếu đã Release).</summary>
        public static void Invalidate(string normalizedPath, InvalidateReason reason)
        {
            if (!_entries.TryRemove(normalizedPath, out var entry)) return;
            switch (reason)
            {
                case InvalidateReason.Changed: Interlocked.Increment(ref _invalidatedChanged); break;
                case InvalidateReason.Closed: Interlocked.Increment(ref _invalidatedClosed); break;
                default: Interlocked.Increment(ref _invalidatedEdited); break;
            }
            entry.Release();
        }

        /// <summary>Bỏ bộ đệm của các file không còn mở.</summary>
        public static void ReleaseExcept(Func<string, bool> isActive)
        {
            foreach (var key in _entries.Keys)
                if (!isActive(key)) Invalidate(key, InvalidateReason.Closed);
        }
    }

    /// <summary>
    /// Bộ đệm cả file chia khối <see cref="BlockSize"/>. Khối chưa có → đọc ngay khối đó (lệnh của trang đang
    /// xem); luồng nền lấp dần các khối còn thiếu theo thứ tự, mỗi lần 1 khối, và đứng chờ khi có lệnh đọc của
    /// trang đang chờ. Khối đã có thì không bao giờ đổi → đọc ra không cần khoá. Mỗi khối chỉ được đọc qua mạng
    /// 1 lần: khối đang được luồng khác đọc (luồng nền, hoặc bản PDFium/iText khác) thì chờ luồng đó.
    ///
    /// An toàn đa luồng: PDFium (dưới gate toàn cục) và iText (PdfLayerService, ngoài gate) có thể đọc cùng lúc.
    /// </summary>
    public sealed class PdfBlockCache
    {
        /// <summary>Cỡ khối — cũng là cỡ mỗi lần đọc nền. Đo trên ổ P: khối 256 KB cho độ trễ trang tốt nhất
        /// (khối nhỏ hơn: quá nhiều lần hỏi-đáp; lớn hơn: lệnh của trang chờ khối nền lâu hơn).</summary>
        public const int BlockSize = 256 * 1024;

        private readonly Func<bool, IBlockFile> _open;
        // Nội dung file nằm trong file tạm cục bộ (tự xoá khi đóng handle, kể cả khi app crash): RAM private ~0, hệ điều hành
        // giữ trang đệm và tự thu hồi khi cần. Đọc lại nhanh như đọc RAM (đĩa cục bộ), không chịu độ trễ mạng.
        private readonly SafeFileHandle _store;
        // Trạng thái từng khối: Missing → Loading (đúng 1 luồng đang đọc) → Present (ghi bằng Volatile.Write SAU khi
        // chép dữ liệu, không bao giờ đổi nữa). Luồng cần khối đang Loading thì chờ luồng kia, không đọc trùng.
        private const int Missing = 0, Present = 1, Loading = 2;
        private readonly int[] _present;
        private int _presentCount;
        private readonly object _lock = new();
        private int _foregroundWaiting;
        /// <summary>Khối ngay sau khối mà trang vừa phải đọc gấp (-1 = không có) — luồng nền nhảy tới đó.</summary>
        private int _followBlock = -1;

        /// <summary>Trang cần khối cách vị trí đọc nền quá mức này (8 × 256 KB = 2 MB) thì luồng nền nhảy theo.</summary>
        private const int FollowDistanceBlocks = 8;

        /// <summary>Khi trang đang xem hụt 1 khối, đọc thêm tối đa chừng này khối Missing liền sau trong CÙNG lệnh đọc:
        /// nội dung 1 trang nằm liền nhau, PDFium đọc tuần tự, mỗi lần hụt qua mạng chịu ~100 ms độ trễ. Đọc nền không
        /// gộp (đo 28/09: làm khối gấp chậm hơn); chỉ lệnh của trang mới đọc trước. XTPDF_FG_AHEAD đổi số này để đo.</summary>
        internal static readonly int ForegroundAheadBlocks =
            int.TryParse(Environment.GetEnvironmentVariable("XTPDF_FG_AHEAD"), out int ahead) ? Math.Clamp(ahead, 0, 31) : 3;
        private readonly IBlockFile _file;          // cho lệnh đọc của trang (đọc theo vị trí, không seek chung)
        private readonly CancellationTokenSource _stop = new();
        private readonly Action _onFreed;
        private int _refs = 1;

        internal PdfBlockCache(string path, long length, DateTime lastWriteUtc, Action onFreed)
            : this(length, lastWriteUtc, onFreed, sequential => new FileBlockFile(path, sequential))
        {
        }

        /// <param name="open">Mở 1 nguồn đọc (true = cho luồng nền, đọc tuần tự). PdfBench truyền bản đọc không
        /// qua cache Windows / giả lập ổ mạng để đo đúng code này.</param>
        internal PdfBlockCache(long length, DateTime lastWriteUtc, Action onFreed, Func<bool, IBlockFile> open)
        {
            Length = length;
            LastWriteUtc = lastWriteUtc;
            _onFreed = onFreed;
            _open = open;
            _store = File.OpenHandle(
                Path.Combine(Path.GetTempPath(), $"XTPdfCache_{Environment.ProcessId}_{Guid.NewGuid():N}.tmp"),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                FileOptions.DeleteOnClose | FileOptions.RandomAccess, length);
            _present = new int[(int)((length + BlockSize - 1) / BlockSize)];
            _file = open(false);
        }

        public long Length { get; }
        public DateTime LastWriteUtc { get; }
        public bool IsComplete => Volatile.Read(ref _presentCount) == _present.Length;

        // Chế độ lười (ổ cục bộ): không có luồng đọc nền, khối chỉ vào file tạm khi PDFium đọc tới. Giao diện coi như "đã sẵn sàng".
        private volatile bool _lazy;
        public bool IsLazy => _lazy;

        // File tạm thưa: ghi khối ở offset xa không bắt NTFS ghi số 0 cho cả đoạn phía trước (nhảy tới cuối file không tốn ghi hàng trăm MB).
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr handle, uint code, IntPtr inBuf, uint inSize, IntPtr outBuf, uint outSize, out uint returned, IntPtr overlapped);

        internal void SetLazy()
        {
            try
            {
                bool added = false;
                _store.DangerousAddRef(ref added);
                try { DeviceIoControl(_store.DangerousGetHandle(), 0x000900C4 /* FSCTL_SET_SPARSE */, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero); }
                finally { if (added) _store.DangerousRelease(); }
            }
            catch { /* không đánh dấu được: vẫn đúng, chỉ có thể ghi dư khi nhảy xa */ }
            _lazy = true;
        }

        internal bool TryAddRef()
        {
            while (true)
            {
                int refs = Volatile.Read(ref _refs);
                if (refs <= 0) return false;
                if (Interlocked.CompareExchange(ref _refs, refs + 1, refs) == refs) return true;
            }
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref _refs) != 0) return;
            _stop.Cancel();
            lock (_lock) Monitor.PulseAll(_lock);
            _file.Dispose();
            _store.Dispose();
            _onFreed();
        }

        /// <summary>Chép [position, position+count) sang bộ nhớ native (callback m_GetBlock của PDFium).</summary>
        public unsafe bool CopyTo(long position, IntPtr destination, int count)
        {
            if (!EnsureRange(position, count)) return false;
            return ReadSmall(new Span<byte>((void*)destination, count), position);
        }

        /// <summary>Chép [position, position+count) sang mảng (iText đọc /OCProperties).</summary>
        public bool CopyTo(long position, byte[] destination, int offset, int count)
        {
            if (!EnsureRange(position, count)) return false;
            return ReadSmall(destination.AsSpan(offset, count), position);
        }

        // Cửa sổ đọc trước theo luồng: PDFium và iText đọc cực nhiều lần rất nhỏ (iText từng byte; đo 02/10: ~800.000 lệnh đọc, TB ~65 byte,
        // khi mở file 165 MB — Foxit chỉ ~3.000). Mỗi lệnh là 1 RandomAccess.Read; ở đây lần đọc nhỏ được trả từ bộ nhớ, cửa sổ chỉ nạp
        // lại khi ra khỏi vùng. Cửa sổ nằm gọn trong MỘT khối đã Present (khối Present không bao giờ đổi) nên không thể đọc nhầm vùng
        // chưa nạp của file tạm thưa.
        private const int SmallReadLimit = 4096, WindowSize = 64 * 1024;

        private sealed class ReadWindow
        {
            public PdfBlockCache? Owner;
            public long Start;
            public int Length;
            public readonly byte[] Buffer = new byte[WindowSize];
        }

        [ThreadStatic] private static ReadWindow? t_window;

        private bool ReadSmall(Span<byte> destination, long position)
        {
            int count = destination.Length;
            if (count > SmallReadLimit) return ReadStore(destination, position);

            var win = t_window ??= new ReadWindow();
            if (!ReferenceEquals(win.Owner, this) || position < win.Start || position + count > win.Start + win.Length)
            {
                long blockEnd = (position / BlockSize + 1) * BlockSize;
                int length = (int)Math.Min(Math.Min(WindowSize, blockEnd - position), Length - position);
                if (length < count) return ReadStore(destination, position); // cắt ngang ranh giới khối: đọc thẳng
                if (!ReadStore(win.Buffer.AsSpan(0, length), position)) { win.Owner = null; return false; }
                win.Owner = this; win.Start = position; win.Length = length;
            }
            win.Buffer.AsSpan((int)(position - win.Start), count).CopyTo(destination);
            return true;
        }

        private bool ReadStore(Span<byte> destination, long position)
        {
            try
            {
                int total = 0;
                while (total < destination.Length)
                {
                    int n = RandomAccess.Read(_store, destination[total..], position + total);
                    if (n <= 0) return false;
                    total += n;
                }
                return true;
            }
            catch { return false; }
        }

        private bool EnsureRange(long position, int count)
        {
            if (position < 0 || count < 0 || position + count > Length) return false;
            if (count == 0) return true;
            int first = (int)(position / BlockSize), last = (int)((position + count - 1) / BlockSize);
            for (int block = first; block <= last; block++)
                if (Volatile.Read(ref _present[block]) != Present && !ReadBlockNow(block)) return false;
            return true;
        }

        /// <summary>Lệnh của trang đang xem: đọc ngay khối còn thiếu (hoặc chờ luồng đang đọc nó), luồng nền
        /// đứng chờ tới khi xong.</summary>
        private bool ReadBlockNow(int block)
        {
            Interlocked.Increment(ref _foregroundWaiting);
            // Người dùng vừa tới vùng chưa đọc (vd bấm End ngay sau khi mở) → luồng nền đọc tiếp từ ĐÂY thay vì tiếp tục
            // tuần tự ở đầu file: các trang quanh đó có mặt trong RAM sớm hơn, phần còn lại lấp sau.
            Volatile.Write(ref _followBlock, block + 1);
            long start = Stopwatch.GetTimestamp();
            byte[]? scratch = null;
            try
            {
                if (!TryClaim(block, wait: true)) return Volatile.Read(ref _present[block]) == Present;
                int count = 1;
                while (count <= ForegroundAheadBlocks && block + count < _present.Length && TryClaim(block + count, wait: false))
                    count++;
                scratch = ArrayPool<byte>.Shared.Rent(BlockSize * count);
                bool ok = false;
                try
                {
                    ok = ReadFromFile(_file, block, scratch, count);
                }
                finally
                {
                    bool stored = true;
                    for (int k = 0; k < count; k++)
                        stored &= Finish(block + k, ok ? scratch : null, k * BlockSize) || !ok; // lỗi đọc: trả khối về Missing, luồng đang chờ tự thử đọc lại
                    ok &= stored;
                }
                if (ok)
                {
                    RenderDiagnostics.FileBlockRead.Record(start);
                    DiagnosticsLog.Slow("khối cần gấp", Stopwatch.GetElapsedTime(start).TotalMilliseconds, $"khối {block}");
                }
                return ok;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Event($"khối cần gấp {block} lỗi: {ex.Message}");
                return false;
            }
            finally
            {
                if (scratch != null) ArrayPool<byte>.Shared.Return(scratch);
                if (Interlocked.Decrement(ref _foregroundWaiting) == 0)
                    lock (_lock) Monitor.PulseAll(_lock);
            }
        }

        /// <summary>Nhận quyền đọc khối (Missing → Loading). true = luồng này phải đọc rồi gọi <see cref="Finish"/>.
        /// false = khối đã có (sau khi chờ luồng khác đọc xong nếu <paramref name="wait"/>), hoặc — khi không chờ —
        /// đang có luồng khác đọc.</summary>
        private bool TryClaim(int block, bool wait)
        {
            lock (_lock)
            {
                while (true)
                {
                    int state = _present[block];
                    if (state == Present) return false;
                    if (state == Missing)
                    {
                        _present[block] = Loading;
                        return true;
                    }
                    if (!wait) return false;
                    Monitor.Wait(_lock); // Finish của luồng đang đọc sẽ PulseAll
                }
            }
        }

        private bool ReadFromFile(IBlockFile file, int block, byte[] scratch, int blockCount = 1)
        {
            long offset = (long)block * BlockSize;
            int size = (int)Math.Min((long)BlockSize * blockCount, Length - offset), read = 0;
            while (read < size)
            {
                int n = file.Read(offset + read, scratch.AsSpan(read, size - read));
                if (n <= 0) return false; // file bị cắt ngắn giữa chừng
                read += n;
            }
            return true;
        }

        /// <summary>Kết thúc lượt đọc đã <see cref="TryClaim"/>: có dữ liệu → Present, lỗi (null) → Missing.</summary>
        private bool Finish(int block, byte[]? scratch, int scratchOffset = 0)
        {
            long offset = (long)block * BlockSize;
            int size = (int)Math.Min(BlockSize, Length - offset);
            bool stored = false;
            if (scratch != null)
            {
                // Luồng này đang giữ Loading nên không ai khác ghi vùng này; người đọc chỉ đọc sau khi thấy Present.
                try { RandomAccess.Write(_store, scratch.AsSpan(scratchOffset, size), offset); stored = true; }
                catch { /* ghi file tạm lỗi (đĩa đầy, handle đã đóng): trả khối về Missing */ }
            }
            lock (_lock)
            {
                if (stored)
                {
                    Volatile.Write(ref _present[block], Present);
                    _presentCount++;
                }
                else
                {
                    _present[block] = Missing;
                }
                Monitor.PulseAll(_lock);
            }
            return stored;
        }

        internal void StartBackgroundRead()
            => Task.Factory.StartNew(BackgroundRead, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        /// <summary>
        /// Đọc nền mọi khối còn thiếu, nhanh hết mức đường truyền, mỗi lượt 1 khối; đứng chờ khi có trang đang chờ khối.
        /// Thứ tự: tuần tự từ đầu file, nhưng khi trang vừa phải đọc gấp 1 khối ở xa (người dùng nhảy tới vùng chưa đọc)
        /// thì nhảy tới ngay sau khối đó, đọc tiếp tới cuối file, rồi quay vòng lấp phần còn thiếu.
        /// </summary>
        private void BackgroundRead()
        {
            long start = Stopwatch.GetTimestamp();
            var token = _stop.Token;
            byte[] scratch = new byte[BlockSize];
            try
            {
                using var handle = _open(true);
                int cursor = 0;
                while (!token.IsCancellationRequested)
                {
                    int follow = Interlocked.Exchange(ref _followBlock, -1);
                    if (follow >= 0 && follow < _present.Length && Math.Abs(follow - cursor) > FollowDistanceBlocks)
                        cursor = follow;

                    int block = NextMissingBlock(cursor);
                    if (block < 0)
                    {
                        if (IsComplete) break;
                        // Còn khối đang do trang đọc dở: chờ nó xong (lỗi đọc thì khối về Missing, nền đọc lại).
                        lock (_lock) Monitor.Wait(_lock, 50);
                        continue;
                    }
                    lock (_lock)
                    {
                        // Nhường trang đang xem: không chiếm đường truyền khi nó đang chờ khối.
                        while (Volatile.Read(ref _foregroundWaiting) > 0 && !token.IsCancellationRequested)
                            Monitor.Wait(_lock, 50);
                    }
                    if (token.IsCancellationRequested) return;
                    cursor = block + 1;
                    if (!TryClaim(block, wait: false)) continue; // trang vừa nhận đọc khối này
                    bool ok = false, stored = false;
                    try
                    {
                        ok = ReadFromFile(handle, block, scratch);
                    }
                    finally
                    {
                        stored = Finish(block, ok ? scratch : null);
                    }
                    if (!ok || !stored) { DiagnosticsLog.Event($"đọc nền dừng: khối {block} {(ok ? "ghi file tạm lỗi" : "đọc lỗi")}"); return; }
                }
                if (IsComplete)
                {
                    RenderDiagnostics.FileBufferRead.Record(start);
                    DiagnosticsLog.Event($"file → đĩa tạm xong {Stopwatch.GetElapsedTime(start).TotalMilliseconds:0} ms");
                }
            }
            catch (Exception ex)
            {
                // Lỗi đọc nền (mạng rớt, file bị xoá…): dừng nền, lệnh của trang vẫn tự đọc khối khi cần.
                DiagnosticsLog.Event("đọc nền dừng do lỗi: " + ex.Message);
            }
        }

        /// <summary>Khối Missing đầu tiên từ <paramref name="from"/> tới cuối file, rồi vòng lại từ đầu; -1 = không còn.</summary>
        private int NextMissingBlock(int from)
        {
            int n = _present.Length;
            for (int i = 0; i < n; i++)
            {
                int block = (from + i) % n;
                if (Volatile.Read(ref _present[block]) == Missing) return block;
            }
            return -1;
        }

        /// <summary>Phần trăm file đã có trong RAM (thanh trạng thái / bảng Debug).</summary>
        public double LoadedFraction => _lazy || _present.Length == 0 ? 1 : Volatile.Read(ref _presentCount) / (double)_present.Length;
    }

    /// <summary>Nguồn đọc theo vị trí của <see cref="PdfBlockCache"/>.</summary>
    internal interface IBlockFile : IDisposable
    {
        /// <summary>Đọc tối đa destination.Length byte tại offset; trả số byte đọc được (0 = hết file).</summary>
        int Read(long offset, Span<byte> destination);
    }

    internal sealed class FileBlockFile(string path, bool sequential) : IBlockFile
    {
        // FileShare.ReadWrite|Delete: không chặn các thao tác khác trên file (chúng tự đóng lease qua
        // SuspendDocumentAsync + Invalidate trước khi ghi).
        private readonly SafeFileHandle _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, sequential ? FileOptions.SequentialScan : FileOptions.RandomAccess);

        public int Read(long offset, Span<byte> destination) => RandomAccess.Read(_handle, destination, offset);
        public void Dispose() => _handle.Dispose();
    }
}
