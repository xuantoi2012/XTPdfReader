using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace XTPdfMergeApp.Services
{
    /// <summary>
    /// Nguồn dữ liệu cho FPDF_LoadCustomDocument: "file gốc + phần nối thêm trong RAM". Phần nối thêm là
    /// incremental update đổi /D/ON, /D/OFF (xem <see cref="PdfLayerService.BuildVisibilityTail"/>), hoặc rỗng
    /// khi file đang ở trạng thái layer mặc định. File gốc đọc qua bộ đệm khối <see cref="PdfBlockCache"/>
    /// (bước #0) — hoặc thẳng từ file nếu file quá lớn để đệm. File trên đĩa không bị sửa.
    ///
    /// Phải sống tới SAU FPDF_CloseDocument (PDFium còn gọi callback đọc trong suốt đời document); mọi lệnh
    /// PDFium đã tuần tự qua gate toàn cục nên callback không bị gọi song song.
    /// </summary>
    public sealed class LayeredDocumentSource : IDisposable
    {
        // struct FPDF_FILEACCESS { unsigned long m_FileLen; int (*m_GetBlock)(void*, unsigned long,
        //                          unsigned char*, unsigned long); void* m_Param; }
        // "unsigned long" = 32 bit trên Windows, 64 bit trên Linux → CULong khớp cả hai.
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeFileAccess
        {
            public CULong FileLength;
            public IntPtr GetBlock;
            public IntPtr Param;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetBlockCallback(IntPtr param, CULong position, IntPtr buffer, CULong size);

        private readonly FileStream? _file;
        /// <summary>Bộ đệm khối của file gốc — nguồn giữ 1 tham chiếu, trả khi Dispose.</summary>
        private PdfBlockCache? _cache;
        private readonly long _originalLength;
        private readonly byte[] _tail;
        private readonly GetBlockCallback _callback; // giữ tham chiếu: GC thu delegate = crash native
        private IntPtr _fileAccess;
        private byte[] _scratch = Array.Empty<byte>();

        public LayeredDocumentSource(string path, long originalLength, byte[] tail)
            : this(originalLength, tail,
                // FileShare.ReadWrite|Delete: không chặn các thao tác khác trên file (chúng vẫn tự đóng lease
                // qua SuspendDocumentAsync trước khi ghi).
                new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess), null)
        {
            _scratch = new byte[64 * 1024];
        }

        /// <summary>Đọc file gốc qua bộ đệm khối. Nhận quyền sở hữu 1 tham chiếu của <paramref name="cache"/>.</summary>
        public LayeredDocumentSource(PdfBlockCache cache, byte[] tail)
            : this(cache.Length, tail, null, cache)
        {
        }

        private LayeredDocumentSource(long originalLength, byte[] tail, FileStream? file, PdfBlockCache? cache)
        {
            _file = file;
            _cache = cache;
            _originalLength = originalLength;
            _tail = tail;
            TotalLength = originalLength + tail.Length;
            if (Environment.OSVersion.Platform == PlatformID.Win32NT && TotalLength > uint.MaxValue)
            {
                _file?.Dispose();
                throw new NotSupportedException("FPDF_LoadCustomDocument trên Windows chỉ nhận file < 4 GB (unsigned long 32 bit).");
            }

            _callback = ReadBlock;
            _fileAccess = Marshal.AllocHGlobal(Marshal.SizeOf<NativeFileAccess>());
            Marshal.StructureToPtr(new NativeFileAccess
            {
                FileLength = new CULong((nuint)TotalLength),
                GetBlock = Marshal.GetFunctionPointerForDelegate(_callback),
                Param = IntPtr.Zero
            }, _fileAccess, false);
        }

        public long TotalLength { get; }
        /// <summary>Con trỏ FPDF_FILEACCESS* truyền cho FPDF_LoadCustomDocument.</summary>
        public IntPtr FileAccessPointer => _fileAccess;

        private int ReadBlock(IntPtr param, CULong position, IntPtr buffer, CULong size)
        {
            try
            {
                long pos = (long)position.Value, count = (long)size.Value;
                if (pos < 0 || count < 0 || pos + count > TotalLength) return 0;
                long written = 0;
                while (written < count)
                {
                    long at = pos + written;
                    int chunk;
                    if (at < _originalLength)
                    {
                        if (_cache != null)
                        {
                            chunk = (int)Math.Min(count - written, _originalLength - at);
                            if (!_cache.CopyTo(at, buffer + (int)written, chunk)) return 0;
                            written += chunk;
                            continue;
                        }
                        chunk = (int)Math.Min(Math.Min(count - written, _originalLength - at), _scratch.Length);
                        _file!.Position = at;
                        int read = _file.ReadAtLeast(_scratch.AsSpan(0, chunk), chunk, throwOnEndOfStream: false);
                        if (read != chunk) return 0; // file gốc bị cắt ngắn giữa chừng
                        Marshal.Copy(_scratch, 0, buffer + (int)written, chunk);
                    }
                    else
                    {
                        int tailOffset = (int)(at - _originalLength);
                        chunk = (int)Math.Min(count - written, _tail.Length - tailOffset);
                        Marshal.Copy(_tail, tailOffset, buffer + (int)written, chunk);
                    }
                    written += chunk;
                }
                return 1;
            }
            catch
            {
                return 0;
            }
        }

        public void Dispose()
        {
            if (_fileAccess != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_fileAccess);
                _fileAccess = IntPtr.Zero;
            }
            _file?.Dispose();
            Interlocked.Exchange(ref _cache, null)?.Release();
            _scratch = Array.Empty<byte>();
        }
    }
}
