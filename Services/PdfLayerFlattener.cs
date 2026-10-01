using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using iText.IO.Source;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser.Util;

namespace XTPdfMergeApp.Services
{
    /// <summary>
    /// "Flatten" theo View layer: xoá HẲN nội dung của các layer đang ẩn khỏi trang (khối <c>/OC … BDC … EMC</c>, XObject và annotation có /OC ẩn),
    /// đổi khối layer đang hiện thành marked content thường và bỏ /OCProperties — file kết quả không còn danh sách layer để bật/tắt.
    /// Trang có ảnh nội tuyến (BI…EI) hoặc nội dung không phân tích được thì trả false; người gọi phải báo lỗi và giữ nguyên file đích.
    /// </summary>
    public static class PdfLayerFlattener
    {
        private static readonly PdfName OC = new("OC");
        private static readonly PdfName OCMD = new("OCMD");
        private static readonly PdfName Properties = PdfName.Properties;
        private static readonly PdfName VE = new("VE");

        /// <summary>Ghi <paramref name="outputPath"/> = <paramref name="inputPath"/> đã bỏ nội dung các layer tên trong <paramref name="hiddenNames"/>. Trả false nếu không làm được (file không đổi).</summary>
        public static bool Flatten(string inputPath, string outputPath, IReadOnlySet<string> hiddenNames)
        {
            try
            {
                PdfFileTransaction.Run(new[] { outputPath }, (_, stage) =>
                {
                    if (!FlattenCore(inputPath, stage, hiddenNames)) throw new IOException("Could not flatten the PDF layer content.");
                });
                return true;
            }
            catch { return false; }
        }

        private static bool FlattenCore(string inputPath, string outputPath, IReadOnlySet<string> hiddenNames)
        {
            try
            {
                using (var doc = new PdfDocument(PdfSecurityService.AuthorizedReaderFor(inputPath, PdfPermissionOperation.Copy), new PdfWriter(outputPath, new WriterProperties().SetFullCompressionMode(true))))
                {
                    var hiddenOcgs = HiddenOcgs(doc, hiddenNames);
                    var visited = new HashSet<PdfStream>();
                    for (int i = 1; i <= doc.GetNumberOfPages(); i++)
                    {
                        var page = doc.GetPage(i);
                        var resources = page.GetPdfObject().GetAsDictionary(PdfName.Resources) ?? new PdfDictionary();
                        byte[]? filtered = Filter(page.GetContentBytes(), resources, hiddenOcgs, doc);
                        if (filtered == null) throw new InvalidOperationException("page content not filterable");
                        var stream = new PdfStream(filtered);
                        stream.SetCompressionLevel(9);
                        page.GetPdfObject().Put(PdfName.Contents, stream.MakeIndirect(doc));
                        FilterForms(resources, hiddenOcgs, doc, visited);
                        RemoveHiddenAnnotations(page, hiddenOcgs);
                    }
                    doc.GetCatalog().GetPdfObject().Remove(PdfName.OCProperties);
                }
                return true;
            }
            catch
            {
                try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
                return false;
            }
        }

        private static HashSet<PdfDictionary> HiddenOcgs(PdfDocument doc, IReadOnlySet<string> hiddenNames)
        {
            var result = new HashSet<PdfDictionary>();
            var ocgs = doc.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.OCProperties)?.GetAsArray(PdfName.OCGs);
            for (int i = 0; ocgs != null && i < ocgs.Size(); i++)
                if (ocgs.GetAsDictionary(i) is { } ocg && hiddenNames.Contains(ocg.GetAsString(PdfName.Name)?.ToUnicodeString() ?? "(unnamed)"))
                    result.Add(ocg);
            return result;
        }

        /// <summary>true nếu dictionary OCG/OCMD này đang ẩn.</summary>
        private static bool IsHidden(PdfDictionary? oc, HashSet<PdfDictionary> hidden)
        {
            if (oc == null) return false;
            if (!OCMD.Equals(oc.GetAsName(PdfName.Type))) return hidden.Contains(oc);
            if (oc.Get(VE) != null) throw new InvalidOperationException("Optional-content visibility expressions are not supported by layer flattening.");
            var members = new List<PdfDictionary>();
            var value = oc.Get(PdfName.OCGs);
            if (value is PdfDictionary single) members.Add(single);
            else if (value is PdfArray array)
                for (int i = 0; i < array.Size(); i++) if (array.GetAsDictionary(i) is { } d) members.Add(d);
            if (members.Count == 0) return false;
            string policy = oc.GetAsName(PdfName.P)?.GetValue() ?? "AnyOn";
            bool anyOn = members.Any(m => !hidden.Contains(m)), allOn = members.All(m => !hidden.Contains(m));
            bool anyOff = members.Any(hidden.Contains), allOff = members.All(hidden.Contains);
            bool visible = policy switch { "AllOn" => allOn, "AnyOff" => anyOff, "AllOff" => allOff, _ => anyOn };
            return !visible;
        }

        private static void FilterForms(PdfDictionary resources, HashSet<PdfDictionary> hidden, PdfDocument doc, HashSet<PdfStream> visited)
        {
            var xobjects = resources.GetAsDictionary(PdfName.XObject);
            if (xobjects == null) return;
            foreach (var key in xobjects.KeySet().ToList())
            {
                var form = xobjects.GetAsStream(key);
                if (form == null || !PdfName.Form.Equals(form.GetAsName(PdfName.Subtype)) || !visited.Add(form)) continue;
                var formResources = form.GetAsDictionary(PdfName.Resources) ?? resources;
                byte[]? filtered = Filter(form.GetBytes(), formResources, hidden, doc);
                if (filtered == null) throw new InvalidOperationException("form content not filterable");
                form.SetData(filtered);
                FilterForms(formResources, hidden, doc, visited);
            }
        }

        private static void RemoveHiddenAnnotations(PdfPage page, HashSet<PdfDictionary> hidden)
        {
            foreach (var annotation in page.GetAnnotations().ToList())
                if (IsHidden(annotation.GetPdfObject().GetAsDictionary(OC), hidden)) page.RemoveAnnotation(annotation);
        }

        /// <summary>Lọc 1 content stream; null nếu gặp thứ không xử lý được (ảnh nội tuyến, lỗi cú pháp).</summary>
        private static byte[]? Filter(byte[] content, PdfDictionary resources, HashSet<PdfDictionary> hidden, PdfDocument doc)
        {
            var properties = resources.GetAsDictionary(Properties);
            var xobjects = resources.GetAsDictionary(PdfName.XObject);
            var output = new MemoryStream();
            var writer = new PdfOutputStream(output);
            var operands = new List<PdfObject>();
            var parser = new PdfCanvasParser(new PdfTokenizer(new RandomAccessFileOrArray(new RandomAccessSourceFactory().CreateSource(content))));
            int ghostDepth = 0; // > 0: trong khối layer ẩn — giữ mọi toán tử đổi trạng thái (nét đứt, màu, ma trận…) nhưng bỏ phần VẼ

            void Emit(IReadOnlyList<PdfObject> ops, int count, string op)
            {
                for (int i = 0; i < count; i++) { writer.Write(ops[i]); writer.WriteSpace(); }
                writer.WriteBytes(System.Text.Encoding.ASCII.GetBytes(op + "\n"));
            }

            try
            {
                while (parser.Parse(operands).Count > 0)
                {
                    string op = operands[^1].ToString() ?? "";
                    int count = operands.Count - 1;
                    if (op is "BI" or "ID" or "EI") return null;

                    if (op is "BDC" or "BMC")
                    {
                        bool isOc = op == "BDC" && count >= 2 && OC.Equals(operands[0]) && operands[1] is PdfName;
                        bool hiddenBlock = isOc && IsHidden(properties?.GetAsDictionary((PdfName)operands[1]), hidden);
                        if (ghostDepth > 0 || hiddenBlock)
                        {
                            ghostDepth++;
                            writer.Write(operands[0]); writer.WriteSpace(); writer.WriteBytes(System.Text.Encoding.ASCII.GetBytes("BMC\n"));
                        }
                        else if (isOc)
                        {
                            writer.Write(OC); writer.WriteSpace(); writer.WriteBytes(System.Text.Encoding.ASCII.GetBytes("BMC\n"));
                        }
                        else Emit(operands, count, op);
                        continue;
                    }
                    if (op == "EMC")
                    {
                        if (ghostDepth > 0) ghostDepth--;
                        Emit(operands, count, op);
                        continue;
                    }

                    if (ghostDepth > 0)
                    {
                        switch (op)
                        {
                            case "S": case "s": case "f": case "F": case "f*": case "B": case "B*": case "b": case "b*":
                                writer.WriteBytes(System.Text.Encoding.ASCII.GetBytes("n\n")); continue; // bỏ vẽ, vẫn kết thúc đường
                            case "Tj": case "TJ": case "sh": case "Do": continue;
                            case "'": writer.WriteBytes(System.Text.Encoding.ASCII.GetBytes("T*\n")); continue;
                            case "\"":
                                if (count >= 2) { writer.Write(operands[0]); writer.WriteBytes(System.Text.Encoding.ASCII.GetBytes(" Tw ")); writer.Write(operands[1]); writer.WriteBytes(System.Text.Encoding.ASCII.GetBytes(" Tc T*\n")); }
                                continue;
                        }
                    }
                    else if (op == "Do" && count >= 1 && operands[0] is PdfName xname)
                    {
                        var xobject = xobjects?.GetAsStream(xname);
                        if (xobject != null && IsHidden(xobject.GetAsDictionary(OC), hidden)) continue;
                    }

                    Emit(operands, count, op);
                }
                if (ghostDepth > 0) return null; // khối ẩn không đóng: nội dung lạ
            }
            catch { return null; }
            return output.ToArray();
        }
    }
}
