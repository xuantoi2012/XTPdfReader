using System;
using System.IO;
using System.Text;
using iText.IO.Source;
using iText.Kernel.Pdf;

namespace XTPdfMergeApp.Services
{
    /// <summary>
    /// One read-only iText document per open source file, kept open while the file is in use so that reading a page's
    /// annotations or copying an annotation's appearance does not re-parse the whole file (165 MB CAD sets). Reads go
    /// through the shared block cache when there is one, otherwise through a file handle that does not block writers.
    /// Every use must hold <see cref="Sync"/>; <see cref="Close"/> before the file is rewritten.
    /// </summary>
    internal sealed class AnnotationSourceReader
    {
        private readonly string _path;
        private PdfDocument? _document;
        private PdfBlockCache? _cache;

        public AnnotationSourceReader(string normalizedPath) => _path = normalizedPath;

        public object Sync { get; } = new();

        /// <summary>The open document (opened on first use). Caller holds <see cref="Sync"/>.</summary>
        public PdfDocument Document()
        {
            if (_document != null) return _document;
            _cache = PdfFileBuffer.Acquire(_path);
            IRandomAccessSource source = _cache != null ? new PdfLayerService.BlockCacheSource(_cache) : new SharedFileSource(_path);
            try
            {
                var properties = new ReaderProperties();
                if (PdfThumbnailService.TryGetDocumentPassword(_path) is { Length: > 0 } password)
                    properties.SetPassword(Encoding.UTF8.GetBytes(password));
                _document = new PdfDocument(new PdfReader(source, properties));
            }
            catch
            {
                source.Close();
                _cache?.Release();
                _cache = null;
                throw;
            }
            return _document;
        }

        public void Close()
        {
            lock (Sync)
            {
                try { _document?.Close(); } catch { /* read-only document */ }
                _document = null;
                _cache?.Release();
                _cache = null;
            }
        }
    }

    /// <summary>Random access over a file with FileShare.ReadWrite | Delete (the file can be saved while it is open).</summary>
    internal sealed class SharedFileSource : IRandomAccessSource
    {
            private readonly FileStream _stream;
            private readonly object _lock = new();

            public SharedFileSource(string path)
                => _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.RandomAccess);

            public int Get(long position)
            {
                lock (_lock)
                {
                    if (position < 0 || position >= _stream.Length) return -1;
                    _stream.Position = position;
                    return _stream.ReadByte();
                }
            }

            public int Get(long position, byte[] bytes, int off, int len)
            {
                lock (_lock)
                {
                    if (position < 0 || position >= _stream.Length) return -1;
                    _stream.Position = position;
                    return _stream.Read(bytes, off, (int)Math.Min(len, _stream.Length - position));
                }
            }

            public long Length() => _stream.Length;
            public void Close() => _stream.Dispose();
    }
}
