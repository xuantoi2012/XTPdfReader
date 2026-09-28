using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;

namespace XTPdfMergeApp.Services
{
    /// <summary>1 chú thích (Typewriter / Note / Highlight) trong file, kể cả do app khác tạo.</summary>
    public sealed record CommentInfo(string Path, int Page, string Name, QuickAnnotationKind Kind, string Author, DateTime? Date, string Text, bool Resolved);

    /// <summary>Đọc mọi chú thích của 1 file cho panel Comments và đổi trạng thái Open / Resolved (/StateModel /Review, /State /Completed).</summary>
    public static class PdfCommentService
    {
        private static readonly PdfName StateModel = new("StateModel");
        private static readonly PdfName State = new("State");
        private static readonly PdfName Review = new("Review");

        public static IReadOnlyList<CommentInfo> ReadAll(string path)
        {
            var result = new List<CommentInfo>();
            using var doc = new PdfDocument(new PdfReader(path));
            for (int p = 1; p <= doc.GetNumberOfPages(); p++)
            {
                var annotations = doc.GetPage(p).GetAnnotations();
                for (int i = 0; i < annotations.Count; i++)
                {
                    var annot = annotations[i];
                    var subtype = annot.GetSubtype();
                    QuickAnnotationKind? kind =
                        PdfName.FreeText.Equals(subtype) ? QuickAnnotationKind.Typewriter :
                        PdfName.Text.Equals(subtype) ? QuickAnnotationKind.Comment :
                        PdfName.Highlight.Equals(subtype) || PdfName.Square.Equals(subtype) ? QuickAnnotationKind.Highlight : null;
                    if (kind == null) continue;
                    var obj = annot.GetPdfObject();
                    string name = annot.GetName()?.ToUnicodeString() is { Length: > 0 } nm ? nm : "#" + i;
                    string author = obj.GetAsString(PdfName.T)?.ToUnicodeString() ?? "";
                    string state = obj.GetAsName(State)?.GetValue() ?? obj.GetAsString(State)?.ToUnicodeString() ?? "";
                    bool resolved = state is "Completed" or "Accepted" or "Cancelled" or "Rejected";
                    result.Add(new CommentInfo(path, p, name, kind.Value, author, ParseDate(obj.GetAsString(PdfName.M)?.ToUnicodeString()
                        ?? obj.GetAsString(PdfName.CreationDate)?.ToUnicodeString()), annot.GetContents()?.ToUnicodeString() ?? "", resolved));
                }
            }
            return result;
        }

        private static DateTime? ParseDate(string? text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string digits = new string(text.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
            if (digits.Length < 8) return null;
            digits = digits.PadRight(14, '0')[..14];
            return DateTime.TryParseExact(digits, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        }

        /// <summary>Đặt / bỏ trạng thái "Resolved" của 1 chú thích (theo tên hoặc "#chỉ số"). Gọi trong <c>PdfPageEditService.EditInPlace</c>.</summary>
        public static void SetResolved(PdfDocument doc, int pageNumber, string name, bool resolved)
        {
            var page = doc.GetPage(pageNumber);
            var annotations = page.GetAnnotations();
            PdfAnnotation? target = annotations.FirstOrDefault(a => a.GetName()?.ToUnicodeString() == name);
            if (target == null && name.StartsWith('#') && int.TryParse(name.AsSpan(1), out int index) && index >= 0 && index < annotations.Count) target = annotations[index];
            if (target == null) return;
            var obj = target.GetPdfObject();
            if (resolved)
            {
                obj.Put(StateModel, new PdfString("Review"));
                obj.Put(State, new PdfString("Completed"));
            }
            else
            {
                obj.Remove(StateModel);
                obj.Remove(State);
            }
            obj.SetModified();
        }
    }
}
