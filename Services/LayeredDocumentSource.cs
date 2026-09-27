using System;
using System.IO;
using System.Runtime.InteropServices;

namespace XTPdfMergeApp.Services
{
    /// <summary>
    /// Nguồn dữ liệu cho FPDF_LoadCustomDocument: "file gốc trên đĩa + phần nối thêm trong RAM" (xem
    /// <see cref="PdfLayerService.BuildVisibilityTail"/>). PDFium đọc từng khối qua callback m_GetBlock nên
    /// không phải nạp cả file CAD lớn vào bộ nhớ như FPDF_LoadMemDocument64, và file trên đĩa không bị sửa.
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

        private readonly FileStream _file;
        private readonly long _originalLength;
        private readonly byte[] _tail;
        private readonly GetBlockCallback _callback; // giữ tham chiếu: GC thu delegate = crash native
        private IntPtr _fileAccess;
        private byte[] _scratch = new byte[64 * 1024];

        public LayeredDocumentSource(string path, long originalLength, byte[] tail)
        {
            // FileShare.ReadWrite|Delete: không chặn các thao tác khác trên file (chúng vẫn tự đóng lease
            // qua SuspendDocumentAsync trước khi ghi).
            _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess);
            _originalLength = originalLength;
            _tail = tail;
            TotalLength = originalLength + tail.Length;
            if (Environment.OSVersion.Platform == PlatformID.Win32NT && TotalLength > uint.MaxValue)
            {
                _file.Dispose();
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
                        chunk = (int)Math.Min(Math.Min(count - written, _originalLength - at), _scratch.Length);
                        _file.Position = at;
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
            _file.Dispose();
            _scratch = Array.Empty<byte>();
        }
    }
}
