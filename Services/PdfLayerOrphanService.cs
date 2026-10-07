using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using iText.Kernel.Pdf;

namespace XTPdfMergeApp.Services
{
    /// <summary>
    /// "Layer mồ côi": OCG mà nội dung trang thật sự dùng (/Properties của trang và Form XObject lồng nhau, /OC của XObject,
    /// chú thích, OCMD) nhưng không có trong /OCProperties/OCGs — thường do ghép file mà không cập nhật danh sách layer.
    /// Trình xem không bật/tắt được chúng (MuPDF chỉ biết layer đã đăng ký). <see cref="Register"/> thêm chúng vào /OCGs và /D/Order.
    /// </summary>
    public static class PdfLayerOrphanService
    {
        private static readonly PdfName OC = new("OC");

        /// <summary>Đọc file rồi tìm layer mồ côi: (id, tên) theo thứ tự gặp.</summary>
        public static IReadOnlyList<(string Id, string Name)> Find(string path)
        {
            var properties = new ReaderProperties();
            if (PdfThumbnailService.TryGetDocumentPassword(path) is { Length: > 0 } password)
                properties.SetPassword(Encoding.UTF8.GetBytes(password));
            using var doc = new PdfDocument(new PdfReader(path, properties));
            return Find(doc).Select(o => (PdfLayerService.IdOf(o), o.GetAsString(PdfName.Name)?.ToUnicodeString() ?? "(unnamed)")).ToList();
        }

        public static List<PdfDictionary> Find(PdfDocument doc)
        {
            var registered = new HashSet<string>();
            var ocgs = doc.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.OCProperties)?.GetAsArray(PdfName.OCGs);
            for (int i = 0; ocgs != null && i < ocgs.Size(); i++)
                if (ocgs.GetAsDictionary(i) is { } d) registered.Add(PdfLayerService.IdOf(d));

            var found = new List<PdfDictionary>();
            var seenIds = new HashSet<string>();
            void Add(PdfDictionary? d)
            {
                if (d == null || !PdfName.OCG.Equals(d.GetAsName(PdfName.Type))) return;
                string id = PdfLayerService.IdOf(d);
                if (id.Length > 0 && !registered.Contains(id) && seenIds.Add(id)) found.Add(d);
            }
            void AddAny(PdfDictionary? d)
            {
                if (d == null) return;
                if (PdfName.OCMD.Equals(d.GetAsName(PdfName.Type)))
                {
                    Add(d.GetAsDictionary(PdfName.OCGs));
                    if (d.GetAsArray(PdfName.OCGs) is { } many)
                        for (int i = 0; i < many.Size(); i++) Add(many.GetAsDictionary(i));
                }
                else Add(d);
            }

            var visited = new HashSet<PdfDictionary>();
            void Walk(PdfDictionary? resources)
            {
                if (resources == null || !visited.Add(resources)) return;
                if (resources.GetAsDictionary(PdfName.Properties) is { } props)
                    foreach (var key in props.KeySet()) AddAny(props.GetAsDictionary(key));
                if (resources.GetAsDictionary(PdfName.XObject) is { } xobjects)
                    foreach (var key in xobjects.KeySet())
                    {
                        if (xobjects.GetAsStream(key) is not { } xo) continue;
                        AddAny(xo.GetAsDictionary(OC));
                        if (PdfName.Form.Equals(xo.GetAsName(PdfName.Subtype))) Walk(xo.GetAsDictionary(PdfName.Resources));
                    }
            }

            for (int p = 1; p <= doc.GetNumberOfPages(); p++)
            {
                var page = doc.GetPage(p).GetPdfObject();
                AddAny(page.GetAsDictionary(OC));
                Walk(page.GetAsDictionary(PdfName.Resources));
                if (page.GetAsArray(PdfName.Annots) is { } annots)
                    for (int i = 0; i < annots.Size(); i++) AddAny(annots.GetAsDictionary(i)?.GetAsDictionary(OC));
            }
            return found;
        }

        /// <summary>Ghi thẳng vào file (nối cuối): đăng ký mọi layer mồ côi vào /OCGs, và vào /D/Order (nhóm "Unregistered layers") nếu file có /Order.</summary>
        public static void RegisterInPlace(string path)
            => PdfPageEditService.EditInPlace(path, "Layers: registered missing layers", doc => Register(doc));

        /// <summary>Đăng ký layer mồ côi vào <paramref name="doc"/> (mở ở append mode). Trả về số layer đã thêm.</summary>
        public static int Register(PdfDocument doc, bool addToOrder = true)
        {
            var orphans = Find(doc);
            if (orphans.Count == 0) return 0;

            var catalog = doc.GetCatalog().GetPdfObject();
            var ocProps = catalog.GetAsDictionary(PdfName.OCProperties);
            if (ocProps == null) { ocProps = new PdfDictionary(); catalog.Put(PdfName.OCProperties, ocProps); }
            var ocgs = ocProps.GetAsArray(PdfName.OCGs);
            if (ocgs == null) { ocgs = new PdfArray(); ocProps.Put(PdfName.OCGs, ocgs); }
            foreach (var ocg in orphans) ocgs.Add(ocg);
            ocgs.SetModified();

            var config = ocProps.GetAsDictionary(PdfName.D);
            if (config == null) { config = new PdfDictionary(); ocProps.Put(PdfName.D, config); }
            if (addToOrder && config.GetAsArray(PdfName.Order) is { } order)
            {
                var group = new PdfArray();
                group.Add(new PdfString("Unregistered layers", iText.IO.Font.PdfEncodings.UNICODE_BIG));
                foreach (var ocg in orphans) group.Add(ocg);
                order.Add(group);
                order.SetModified();
            }
            config.SetModified();
            ocProps.SetModified();
            catalog.SetModified();
            return orphans.Count;
        }
    }
}
