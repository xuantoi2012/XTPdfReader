using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using iText.Kernel.Pdf;

namespace XTPdfMergeApp.Services
{
    /// <summary>1 nút trong cây Layer theo /OCProperties/D/Order. <see cref="OcgId"/> null = nhãn nhóm
    /// (chuỗi đứng đầu 1 mảng con trong /Order), không bật/tắt được.</summary>
    public sealed record PdfLayerNode(string Title, string? OcgId, bool IsLocked, IReadOnlyList<PdfLayerNode> Children)
    {
        /// <summary>Layer do /D/AS (usage application, sự kiện View) của file điều khiển — PDFium áp /AS đè lên
        /// /D/ON-/D/OFF nên không bật/tắt được khi xem (đã đo thật, xem báo cáo).</summary>
        public bool IsUsageControlled { get; init; }
    }

    /// <summary>Layer của 1 file: cây hiển thị + tên + tập tắt mặc định (/D/BaseState + /D/ON + /D/OFF).
    /// OCG định danh bằng "số_object thế_hệ" của tham chiếu gián tiếp — tên layer có thể trùng nhau.</summary>
    public sealed record PdfLayerInfo(
        IReadOnlyList<PdfLayerNode> Roots,
        IReadOnlyDictionary<string, string> Names,
        IReadOnlySet<string> DefaultHidden,
        IReadOnlySet<string> Locked,
        IReadOnlySet<string>? UsageControlled = null)
    {
        public static readonly PdfLayerInfo Empty = new(Array.Empty<PdfLayerNode>(),
            new Dictionary<string, string>(), new HashSet<string>(), new HashSet<string>());
        public bool HasLayers => Names.Count > 0;
    }

    /// <summary>Đọc layer (OCG) bằng iText và sinh phần nối thêm (incremental update) đổi /D/ON, /D/OFF —
    /// PDFium không có API bật/tắt layer, nhưng luôn vẽ theo trạng thái mặc định trong /OCProperties/D,
    /// nên đổi trạng thái mặc định (chỉ trong bộ nhớ, file trên đĩa giữ nguyên) là đủ để ẩn/hiện.</summary>
    public static class PdfLayerService
    {
        private static readonly PdfName Locked = new("Locked");
        private static readonly PdfName BaseState = new("BaseState");
        private static readonly PdfName AS = new("AS");
        private static readonly PdfName Event = new("Event");
        private static readonly PdfName View = new("View");
        private static readonly PdfName ViewState = new("ViewState");
        private static readonly PdfName Usage = new("Usage");

        public static string IdOf(PdfObject ocg)
        {
            var reference = ocg.GetIndirectReference();
            return reference == null ? "" : $"{reference.GetObjNumber()} {reference.GetGenNumber()}";
        }

        public static PdfLayerInfo ReadLayers(string path)
        {
            using var doc = new PdfDocument(new PdfReader(path));
            return ReadLayers(doc);
        }

        public static PdfLayerInfo ReadLayers(PdfDocument doc)
        {
            var ocProperties = doc.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.OCProperties);
            var ocgs = ocProperties?.GetAsArray(PdfName.OCGs);
            if (ocgs == null || ocgs.Size() == 0) return PdfLayerInfo.Empty;

            var names = new Dictionary<string, string>();
            for (int i = 0; i < ocgs.Size(); i++)
            {
                if (ocgs.GetAsDictionary(i) is not { } ocg) continue;
                string id = IdOf(ocg);
                if (id.Length == 0) continue;
                names[id] = ocg.GetAsString(PdfName.Name)?.ToUnicodeString() ?? "(không tên)";
            }

            var config = ocProperties!.GetAsDictionary(PdfName.D);
            var hidden = new HashSet<string>();
            if (PdfName.OFF.Equals(config?.GetAsName(BaseState))) hidden.UnionWith(names.Keys);
            foreach (string id in IdsIn(config?.GetAsArray(PdfName.ON))) hidden.Remove(id);
            foreach (string id in IdsIn(config?.GetAsArray(PdfName.OFF))) if (names.ContainsKey(id)) hidden.Add(id);
            var locked = new HashSet<string>(IdsIn(config?.GetAsArray(Locked)).Where(names.ContainsKey));

            // /D/AS: OCG thuộc 1 mục có /Event /View → trạng thái khi xem lấy từ /Usage/View/ViewState của OCG.
            var usageControlled = new HashSet<string>();
            var autoStates = config?.GetAsArray(AS);
            for (int i = 0; autoStates != null && i < autoStates.Size(); i++)
            {
                if (autoStates.GetAsDictionary(i) is not { } entry || !View.Equals(entry.GetAsName(Event))) continue;
                for (int k = 0; entry.GetAsArray(PdfName.OCGs) is { } list && k < list.Size(); k++)
                {
                    if (list.GetAsDictionary(k) is not { } ocg || IdOf(ocg) is not { Length: > 0 } id || !names.ContainsKey(id)) continue;
                    var viewState = ocg.GetAsDictionary(Usage)?.GetAsDictionary(View)?.GetAsName(ViewState);
                    if (viewState == null) continue; // OCG không khai báo usage View → /AS không tác động
                    usageControlled.Add(id);
                    if (PdfName.OFF.Equals(viewState)) hidden.Add(id); else hidden.Remove(id);
                }
            }

            var order = config?.GetAsArray(PdfName.Order);
            IReadOnlyList<PdfLayerNode> roots = order != null
                ? BuildOrder(order, names, locked, new HashSet<PdfArray>())
                // Không có /Order: liệt kê phẳng theo /OCGs (Acrobat cũng làm vậy).
                : names.Select(n => new PdfLayerNode(n.Value, n.Key, locked.Contains(n.Key), Array.Empty<PdfLayerNode>())).ToList();
            if (usageControlled.Count > 0) roots = MarkUsageControlled(roots, usageControlled);
            return new PdfLayerInfo(roots, names, hidden, locked, usageControlled);
        }

        private static IReadOnlyList<PdfLayerNode> MarkUsageControlled(IReadOnlyList<PdfLayerNode> nodes, IReadOnlySet<string> ids)
            => nodes.Select(n => n with
            {
                IsUsageControlled = n.OcgId != null && ids.Contains(n.OcgId),
                Children = MarkUsageControlled(n.Children, ids)
            }).ToList();

        private static IEnumerable<string> IdsIn(PdfArray? array)
        {
            if (array == null) yield break;
            for (int i = 0; i < array.Size(); i++)
                if (array.GetAsDictionary(i) is { } ocg && IdOf(ocg) is { Length: > 0 } id) yield return id;
        }

        /// <summary>/Order: OCG → 1 nút; mảng đứng NGAY SAU 1 OCG = con của OCG đó; mảng mở đầu bằng
        /// chuỗi = nhóm có nhãn (chuỗi đó), không phải OCG.</summary>
        private static List<PdfLayerNode> BuildOrder(PdfArray order, IReadOnlyDictionary<string, string> names,
            IReadOnlySet<string> locked, HashSet<PdfArray> visiting)
        {
            var result = new List<PdfLayerNode>();
            if (!visiting.Add(order)) return result; // mảng tự tham chiếu (file hỏng)
            for (int i = 0; i < order.Size(); i++)
            {
                var item = order.Get(i, true);
                if (item is PdfDictionary ocg && IdOf(ocg) is { Length: > 0 } id && names.TryGetValue(id, out var title))
                {
                    IReadOnlyList<PdfLayerNode> children = Array.Empty<PdfLayerNode>();
                    if (i + 1 < order.Size() && order.Get(i + 1, true) is PdfArray sub && !StartsWithLabel(sub))
                    {
                        children = BuildOrder(sub, names, locked, visiting);
                        i++;
                    }
                    result.Add(new PdfLayerNode(title, id, locked.Contains(id), children));
                }
                else if (item is PdfArray group)
                {
                    if (StartsWithLabel(group))
                    {
                        string label = group.GetAsString(0)?.ToUnicodeString() ?? "";
                        var rest = new PdfArray();
                        for (int k = 1; k < group.Size(); k++) rest.Add(group.Get(k, false));
                        result.Add(new PdfLayerNode(label, null, false, BuildOrder(rest, names, locked, visiting)));
                    }
                    else result.AddRange(BuildOrder(group, names, locked, visiting));
                }
            }
            visiting.Remove(order);
            return result;
        }

        private static bool StartsWithLabel(PdfArray array) => array.Size() > 0 && array.Get(0, true) is PdfString;

        /// <summary>
        /// Phần NỐI THÊM vào cuối file (incremental update) để /D/ON, /D/OFF đúng bằng <paramref name="hidden"/>.
        /// File gốc + phần này = 1 PDF hợp lệ mà PDFium vẽ đúng trạng thái layer mong muốn. Chỉ giữ phần đuôi
        /// (vài trăm byte) — không chép cả file (file CAD có thể rất lớn), xem <see cref="LayeredDocumentSource"/>.
        /// </summary>
        /// <param name="cache">Bộ đệm khối của file (PdfFileBuffer) — có thì iText đọc qua đó (khối đã có không
        /// phải đọc lại qua ổ mạng, khối thiếu được ưu tiên hơn đọc nền), không mở file riêng.</param>
        public static byte[] BuildVisibilityTail(string path, IReadOnlySet<string> hidden, out long originalLength, PdfBlockCache? cache = null)
        {
            originalLength = cache?.Length ?? new FileInfo(path).Length;
            var tail = new TailOnlyStream(originalLength);
            using (var reader = cache != null
                ? new PdfReader(new BlockCacheSource(cache), new ReaderProperties())
                : new PdfReader(path))
            using (var writer = new PdfWriter(tail))
            using (var doc = new PdfDocument(reader, writer, new StampingProperties().UseAppendMode()))
            {
                var ocProperties = doc.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.OCProperties);
                var ocgs = ocProperties?.GetAsArray(PdfName.OCGs);
                if (ocProperties != null && ocgs != null)
                {
                    var config = ocProperties.GetAsDictionary(PdfName.D);
                    if (config == null)
                    {
                        config = new PdfDictionary();
                        ocProperties.Put(PdfName.D, config);
                    }
                    var on = new PdfArray();
                    var off = new PdfArray();
                    for (int i = 0; i < ocgs.Size(); i++)
                    {
                        var entry = ocgs.Get(i, false);
                        if (ocgs.GetAsDictionary(i) is not { } ocg) continue;
                        (hidden.Contains(IdOf(ocg)) ? off : on).Add(entry);
                    }
                    // CHỈ đổi /ON và /OFF (liệt kê đủ mọi OCG nên /BaseState không còn tác dụng, giữ nguyên).
                    // /AS (usage application) và /Order giữ nguyên byte-for-byte theo file gốc — quyết định
                    // của chủ dự án: tôn trọng cơ chế tự bật/tắt theo usage mà file đã khai báo.
                    config.Put(PdfName.ON, on);
                    config.Put(PdfName.OFF, off);
                    config.SetModified();
                    ocProperties.SetModified();
                }
            }
            return tail.ToArray();
        }

        /// <summary>iText đọc file gốc qua <see cref="PdfBlockCache"/>. Không sở hữu bộ đệm (người gọi giữ tham chiếu).</summary>
        private sealed class BlockCacheSource(PdfBlockCache cache) : iText.IO.Source.IRandomAccessSource
        {
            private readonly byte[] _one = new byte[1];

            public int Get(long position)
            {
                if (position < 0 || position >= cache.Length) return -1;
                if (!cache.CopyTo(position, _one, 0, 1)) throw new IOException("Không đọc được file PDF gốc.");
                return _one[0];
            }

            public int Get(long position, byte[] bytes, int off, int len)
            {
                if (position < 0 || position >= cache.Length) return -1;
                int count = (int)Math.Min(len, cache.Length - position);
                if (!cache.CopyTo(position, bytes, off, count)) throw new IOException("Không đọc được file PDF gốc.");
                return count;
            }

            public long Length() => cache.Length;

            public void Close()
            {
            }
        }

        /// <summary>Stream cho iText ghi bản append-mode: bỏ qua đúng <c>originalLength</c> byte đầu (iText
        /// chép lại nguyên file gốc) và chỉ giữ phần nối thêm.</summary>
        private sealed class TailOnlyStream(long skip) : Stream
        {
            private readonly MemoryStream _tail = new();
            private long _position;
            public byte[] ToArray() => _tail.ToArray();
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => _position;
            public override long Position { get => _position; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count)
            {
                long end = _position + count;
                if (end > skip)
                {
                    int start = (int)Math.Max(0, skip - _position);
                    _tail.Write(buffer, offset + start, count - start);
                }
                _position = end;
            }
        }
    }

    /// <summary>
    /// Trạng thái "layer đang tắt" của từng file đang mở (dùng chung giữa luồng UI và luồng render).
    /// <see cref="GetToken"/> = "phiên bản trạng thái layer" đưa vào key cache thumbnail/trang/tile:
    /// "" khi file đang ở đúng trạng thái mặc định (mở file bình thường), khác "" khi user đã đổi.
    /// </summary>
    public static class PdfLayerStateStore
    {
        private sealed record State(IReadOnlySet<string> Hidden, string Token);

        private static readonly ConcurrentDictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);

        public static event Action<string>? StateChanged;

        private static string Normalize(string path) => Path.GetFullPath(path);

        public static string GetToken(string path)
            => _states.TryGetValue(Normalize(path), out var state) ? state.Token : "";

        /// <summary>Tập layer đang tắt KHÁC mặc định, hoặc null nếu file đang ở trạng thái mặc định.</summary>
        public static IReadOnlySet<string>? GetHiddenOverride(string path, out string token)
        {
            if (_states.TryGetValue(Normalize(path), out var state))
            {
                token = state.Token;
                return state.Hidden;
            }
            token = "";
            return null;
        }

        /// <summary>Đặt tập layer đang tắt. Trả true nếu trạng thái hiển thị thật sự đổi.</summary>
        public static bool SetHidden(string path, IReadOnlySet<string> hidden, IReadOnlySet<string> defaultHidden)
        {
            string key = Normalize(path);
            string oldToken = GetToken(key);
            string token = ComputeToken(hidden, defaultHidden);
            if (token.Length == 0) _states.TryRemove(key, out _);
            else _states[key] = new State(new HashSet<string>(hidden), token);
            if (token == oldToken) return false;
            StateChanged?.Invoke(key);
            return true;
        }

        public static void Forget(string path) => _states.TryRemove(Normalize(path), out _);

        /// <summary>Bỏ trạng thái của các file không còn mở (đóng hẳn file = mở lại về mặc định).</summary>
        public static void ForgetAllExcept(IEnumerable<string> openPaths)
        {
            var keep = new HashSet<string>(openPaths.Select(Normalize), StringComparer.OrdinalIgnoreCase);
            foreach (string key in _states.Keys)
                if (!keep.Contains(key)) _states.TryRemove(key, out _);
        }

        /// <summary>Token ổn định theo TẬP layer tắt (không phụ thuộc thứ tự bật/tắt) — bật/tắt qua lại
        /// quay về đúng token cũ nên trúng lại cache đã vẽ.</summary>
        public static string ComputeToken(IReadOnlySet<string> hidden, IReadOnlySet<string> defaultHidden)
        {
            if (hidden.SetEquals(defaultHidden)) return "";
            string canonical = string.Join(";", hidden.OrderBy(id => id, StringComparer.Ordinal));
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
            return "L" + Convert.ToHexString(hash, 0, 12);
        }
    }
}
