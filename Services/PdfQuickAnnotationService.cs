using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using iText.IO.Font;
using iText.IO.Font.Constants;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Extgstate;
using iText.Kernel.Pdf.Xobject;

namespace XTPdfMergeApp.Services
{
    /// <summary>Other = chú thích của app khác mà app không tự vẽ lại được (polygon, file đính kèm, form…): vẫn hiện
    /// (appearance gốc), chọn/di chuyển/xoá được, không sửa nội dung.</summary>
    public enum QuickAnnotationKind { Typewriter, Comment, Reply, Callout, Highlight, Stamp, Shape, Underline, StrikeOut, Squiggly, Ink, Other }

    /// <summary>
    /// 1 annotation do công cụ sửa nhanh quản lý, mô tả theo toạ độ TRANG HIỂN THỊ chuẩn hoá (xem
    /// <see cref="PdfPageGeometry"/>): (U1, V1) trên-trái, (U2, V2) dưới-phải. Typewriter/Comment chỉ
    /// dùng (U1, V1) làm điểm neo khi tạo — kích thước tự tính theo nội dung/icon.
    /// <see cref="Name"/> = /NM của annotation, dùng để tìm lại khi sửa/xoá/undo.
    /// </summary>
    public sealed record QuickAnnotationSpec(
        string Name, QuickAnnotationKind Kind, int PageNumber,
        double U1, double V1, double U2, double V2, string Text)
    {
        /// <summary>Typewriter: <see cref="TextFormat"/>; Shape: <see cref="ShapeStyle"/>; Highlight: "T|" + line rects ("" = default).</summary>
        public string Format { get; init; } = "";

        /// <summary>Số hiệu đối tượng trong file khi chú thích lấy từ file và appearance gốc còn đúng (chưa sửa nội dung/kiểu):
        /// vẽ bằng appearance gốc, lúc Save chỉ dịch chuyển. 0 = appearance do app tạo lại từ spec.</summary>
        public int ObjectNumber { get; init; }
        public int Generation { get; init; }
        /// <summary>Màu /C (#RRGGBB, "" = mặc định của loại) và độ đục /CA.</summary>
        public string Color { get; init; } = "";
        public double Opacity { get; init; } = 1;
        /// <summary>False: chỉ hiện (form field), không chọn/di chuyển.</summary>
        public bool Selectable { get; init; } = true;
        public string Author { get; init; } = "";
        public DateTime? Date { get; init; }
        public bool Resolved { get; init; }
        /// <summary>Loại PDF gốc (/Subtype) — nhãn cho chú thích loại Other.</summary>
        public string Subtype { get; init; } = "";

        public bool Contains(double u, double v) => u >= U1 && u <= U2 && v >= V1 && v <= V2;

        /// <summary>Dịch chuyển (toạ độ trang hiển thị chuẩn hoá); highlight theo chữ dời luôn các dòng.</summary>
        public QuickAnnotationSpec Translate(double du, double dv)
        {
            string format = Format;
            if (Kind is QuickAnnotationKind.Highlight or QuickAnnotationKind.Underline or QuickAnnotationKind.StrikeOut or QuickAnnotationKind.Squiggly && format.StartsWith("T|", StringComparison.Ordinal))
                format = PdfQuickAnnotationService.EncodeTextHighlight(
                    PdfQuickAnnotationService.TextHighlightRects(format).Select(r => (r.U1 + du, r.V1 + dv, r.U2 + du, r.V2 + dv)));
            else if (Kind == QuickAnnotationKind.Ink && format.StartsWith("I|", StringComparison.Ordinal))
                format = PdfQuickAnnotationService.EncodeInkPoints(PdfQuickAnnotationService.InkPoints(format).Select(p => (p.U + du, p.V + dv)));
            else if (Kind == QuickAnnotationKind.Callout && format.StartsWith("C|", StringComparison.Ordinal))
            {
                // Dời callout = dời cả mũi tên (như Foxit), không chỉ hộp.
                var callout = PdfQuickAnnotationService.DecodeCallout(format);
                format = PdfQuickAnnotationService.EncodeCallout(callout.TipU + du, callout.TipV + dv, callout.TextFormat, callout.Style);
            }
            return this with { U1 = U1 + du, V1 = V1 + dv, U2 = U2 + du, V2 = V2 + dv, Format = format };
        }
    }

    /// <summary>1 bước sửa annotation: gỡ <see cref="Remove"/> (theo Name) rồi thêm <see cref="Add"/>.
    /// Tạo = (null, mới); sửa = (cũ, mới); xoá = (cũ, null). Đảo 2 vế là ra thao tác Undo.</summary>
    public sealed record QuickAnnotationChange(QuickAnnotationSpec? Remove, QuickAnnotationSpec? Add)
    {
        public int PageNumber => (Add ?? Remove)!.PageNumber;
        public QuickAnnotationChange Inverse() => new(Add, Remove);
    }

    /// <summary>Ghi/đọc annotation Typewriter (FreeText), Comment (sticky note /Text) và Highlight bằng
    /// iText. Luôn tự sinh appearance stream (/AP) — PDFium và mọi viewer vẽ đúng như nhau, kể cả chữ
    /// tiếng Việt (font Unicode nhúng subset) và trang có /Rotate.</summary>
    public static class PdfQuickAnnotationService
    {
        public const float TypewriterFontSize = 12f;
        private const float TypewriterLineHeight = 1.2f;
        private const float TypewriterPadding = 2f;
        private const float CommentIconSize = 20f;
        private static readonly PdfName TypewriterIntent = new("FreeTextTypeWriter");
        private static readonly PdfName IntentKey = new("IT");
        private static readonly PdfName FormatKey = new("XTFormat");
        private static readonly PdfName ShapeKey = new("XTShape");

        private static readonly System.Collections.Generic.Dictionary<string, string[]> FontFiles = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Arial"] = new[] { "arial.ttf", "arialbd.ttf", "ariali.ttf", "arialbi.ttf" },
            ["Times New Roman"] = new[] { "times.ttf", "timesbd.ttf", "timesi.ttf", "timesbi.ttf" },
            ["Tahoma"] = new[] { "tahoma.ttf", "tahomabd.ttf", "tahoma.ttf", "tahomabd.ttf" },
            ["Segoe UI"] = new[] { "segoeui.ttf", "segoeuib.ttf", "segoeuii.ttf", "segoeuiz.ttf" },
            ["Calibri"] = new[] { "calibri.ttf", "calibrib.ttf", "calibrii.ttf", "calibriz.ttf" },
            ["Verdana"] = new[] { "verdana.ttf", "verdanab.ttf", "verdanai.ttf", "verdanaz.ttf" },
            ["Courier New"] = new[] { "cour.ttf", "courbd.ttf", "couri.ttf", "courbi.ttf" },
        };

        /// <summary>Embedded Unicode font for a Typewriter format (falls back to the default Unicode font when the family is not installed).</summary>
        private static PdfFont CreateFormatFont(TextFormat format)
        {
            if (FontFiles.TryGetValue(format.Family, out var files))
            {
                string path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), files[(format.Bold ? 1 : 0) + (format.Italic ? 2 : 0)]);
                if (File.Exists(path))
                {
                    try { return PdfFontFactory.CreateFont(path, PdfEncodings.IDENTITY_H, PdfFontFactory.EmbeddingStrategy.PREFER_EMBEDDED); }
                    catch { /* fall through */ }
                }
            }
            return CreateUnicodeFont();
        }

        /// <summary>Font TrueType có đủ dấu tiếng Việt cho typewriter. null = tự dò font hệ thống.</summary>
        public static string? FontPathOverride { get; set; }

        public static PdfPageGeometry GetGeometry(PdfPage page)
        {
            Rectangle crop = page.GetCropBox();
            int rotation = PdfPageEditService.NormalizeRotation(page.GetRotation());
            return new PdfPageGeometry(crop.GetX(), crop.GetY(), crop.GetWidth(), crop.GetHeight(), rotation);
        }

        private static readonly PdfName StateKey = new("State");

        /// <summary>Mọi chú thích hiện được của trang (trừ Popup, Link, chú thích ẩn), kể cả của app khác — thứ tự vẽ như trong /Annots.</summary>
        public static IReadOnlyList<QuickAnnotationSpec> ReadAnnotations(PdfPage page, PdfPageGeometry geometry, int pageNumber)
        {
            var result = new List<QuickAnnotationSpec>();
            var annotations = page.GetAnnotations();
            for (int i = 0; i < annotations.Count; i++)
            {
                var annot = annotations[i];
                var subtype = annot.GetSubtype();
                if (subtype == null || PdfName.Popup.Equals(subtype) || PdfName.Link.Equals(subtype)) continue;
                int flags = annot.GetFlags();
                if ((flags & (PdfAnnotation.HIDDEN | PdfAnnotation.NO_VIEW)) != 0) continue;
                // AutoCAD xuất PDF: chữ dùng font SHX (không nhúng được outline chữ thật) được thay bằng 1 annotation
                // phủ đúng vùng chữ đó (đã thấy tận mắt trên file thật: /Subtype /Square, không phải /Text — AutoCAD
                // không cố định subtype, nên so theo /T thay vì subtype), tên tác giả CỐ ĐỊNH "AutoCAD SHX Text" —
                // chỉ để tìm/copy được chữ đó, không phải ghi chú thật của ai cả. Acrobat/Bluebeam đều ẩn loại này
                // khỏi danh sách comment; bản vẽ kỹ thuật xuất từ AutoCAD có thể có HÀNG NGHÌN cái (mỗi số kích
                // thước/cao độ trên bản vẽ 1 cái) — không lọc sẽ ngập panel Comments (thấy "Comments (4460)" trên
                // file thật 165MB) và hàng nghìn annotation "Other" đó vẫn được chọn/hit-test được, cản việc đặt
                // chú thích mới ở đúng chỗ.
                if (string.Equals(annot.GetPdfObject().GetAsString(PdfName.T)?.ToUnicodeString(), "AutoCAD SHX Text", StringComparison.Ordinal))
                    continue;
                QuickAnnotationKind kind = KindOf(annot) ?? QuickAnnotationKind.Other;
                var rect = annot.GetRectangle()?.ToRectangle();
                if (rect == null || rect.GetWidth() <= 0 && rect.GetHeight() <= 0) continue;
                var (u1, v1, u2, v2) = geometry.UserRectToDisplay(rect.GetLeft(), rect.GetBottom(), rect.GetRight(), rect.GetTop());
                var obj = annot.GetPdfObject();
                string? calloutFormat = null;
                if (kind == QuickAnnotationKind.Callout)
                    (u1, v1, u2, v2, calloutFormat) = ReadCalloutBox(obj, geometry, (u1, v1, u2, v2));
                var reference = obj.GetIndirectReference();
                string name = annot.GetName()?.ToUnicodeString() is { Length: > 0 } nm ? nm
                    : reference != null ? $"#o{reference.GetObjNumber()}_{reference.GetGenNumber()}" : "#i" + i;
                string state = obj.GetAsName(StateKey)?.GetValue() ?? obj.GetAsString(StateKey)?.ToUnicodeString() ?? "";
                result.Add(new QuickAnnotationSpec(name, kind, pageNumber, u1, v1, u2, v2, annot.GetContents()?.ToUnicodeString() ?? "")
                {
                    Format = kind switch
                    {
                        QuickAnnotationKind.Highlight or QuickAnnotationKind.Underline or QuickAnnotationKind.StrikeOut or QuickAnnotationKind.Squiggly => ReadQuadFormat(annot, geometry),
                        QuickAnnotationKind.Shape => obj.GetAsString(ShapeKey)?.ToUnicodeString() ?? "",
                        QuickAnnotationKind.Ink => ReadInkFormat(annot, geometry),
                        QuickAnnotationKind.Callout => calloutFormat!,
                        _ => obj.GetAsString(FormatKey)?.ToUnicodeString() ?? ""
                    },
                    ObjectNumber = reference?.GetObjNumber() ?? 0,
                    Generation = reference?.GetGenNumber() ?? 0,
                    Color = ColorOf(obj.GetAsArray(PdfName.C)),
                    Opacity = obj.GetAsNumber(PdfName.CA)?.DoubleValue() ?? 1,
                    Selectable = !PdfName.Widget.Equals(subtype),
                    Author = obj.GetAsString(PdfName.T)?.ToUnicodeString() ?? "",
                    Date = ParsePdfDate(obj.GetAsString(PdfName.M)?.ToUnicodeString() ?? obj.GetAsString(PdfName.CreationDate)?.ToUnicodeString()),
                    Resolved = state is "Completed" or "Accepted" or "Cancelled" or "Rejected",
                    Subtype = subtype.GetValue()
                });
            }
            return result;
        }

        private static readonly PdfName CalloutBoxKey = new("XTBox");

        /// <summary>Callout đọc từ file: /Rect phủ cả hộp LẪN mũi tên, nên hộp thật lấy từ /XTBox (user-space, dời cùng /Rect
        /// khi Translate) và điểm chỉ lấy từ /CL (điểm đầu) — không tin toạ độ điểm chỉ lưu sẵn trong chuỗi /XTFormat vì
        /// dời chú thích tại chỗ (Translate) chỉ dịch các mảng toạ độ. File cũ chưa có /XTBox: giữ /Rect như trước.</summary>
        private static (double U1, double V1, double U2, double V2, string Format) ReadCalloutBox(PdfDictionary obj, PdfPageGeometry geometry, (double U1, double V1, double U2, double V2) rect)
        {
            var stored = DecodeCallout(obj.GetAsString(FormatKey)?.ToUnicodeString() ?? "");
            double tipU = stored.TipU, tipV = stored.TipV;
            if (obj.GetAsArray(PdfName.CL) is { } cl && cl.Size() >= 2 && cl.GetAsNumber(0) is { } cx && cl.GetAsNumber(1) is { } cy)
            {
                var tip = geometry.UserRectToDisplay(cx.DoubleValue(), cy.DoubleValue(), cx.DoubleValue(), cy.DoubleValue());
                (tipU, tipV) = (tip.U1, tip.V1);
            }
            var box = rect;
            if (obj.GetAsArray(CalloutBoxKey) is { } b && b.Size() == 4 && b.GetAsNumber(0) is { } l && b.GetAsNumber(1) is { } bo && b.GetAsNumber(2) is { } r && b.GetAsNumber(3) is { } t)
                box = geometry.UserRectToDisplay(l.DoubleValue(), bo.DoubleValue(), r.DoubleValue(), t.DoubleValue());
            return (box.U1, box.V1, box.U2, box.V2, EncodeCallout(tipU, tipV, stored.TextFormat, stored.Style));
        }

        private static string ColorOf(PdfArray? c)
        {
            if (c == null) return "";
            double Ch(int i) => Math.Clamp(c.GetAsNumber(i)?.DoubleValue() ?? 0, 0, 1);
            (double r, double g, double b) = c.Size() switch
            {
                1 => (Ch(0), Ch(0), Ch(0)),
                3 => (Ch(0), Ch(1), Ch(2)),
                4 => ((1 - Ch(0)) * (1 - Ch(3)), (1 - Ch(1)) * (1 - Ch(3)), (1 - Ch(2)) * (1 - Ch(3))),
                _ => (-1, -1, -1)
            };
            return r < 0 ? "" : $"#{(int)Math.Round(r * 255):X2}{(int)Math.Round(g * 255):X2}{(int)Math.Round(b * 255):X2}";
        }

        internal static DateTime? ParsePdfDate(string? text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string digits = new string(text.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
            if (digits.Length < 8) return null;
            digits = digits.PadRight(14, '0')[..14];
            return DateTime.TryParseExact(digits, "yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var d) ? d : null;
        }

        /// <summary>A text markup (highlight/underline/strikethrough) keeps its per-line rectangles (from /QuadPoints) so moving it does not turn it into one big block.</summary>
        private static string ReadQuadFormat(PdfAnnotation annot, PdfPageGeometry geometry)
        {
            var subtype = annot.GetSubtype();
            if (!PdfName.Highlight.Equals(subtype) && !PdfName.Underline.Equals(subtype) && !PdfName.StrikeOut.Equals(subtype) && !PdfName.Squiggly.Equals(subtype)) return "";
            var quads = annot.GetPdfObject().GetAsArray(PdfName.QuadPoints);
            // One text line is exactly one quad (8 numbers); requiring 16 silently discarded every single-line markup.
            if (quads == null || quads.Size() < 8) return "";
            var rects = new List<(double, double, double, double)>();
            for (int i = 0; i + 7 < quads.Size(); i += 8)
            {
                var xs = new[] { quads.GetAsNumber(i).FloatValue(), quads.GetAsNumber(i + 2).FloatValue(), quads.GetAsNumber(i + 4).FloatValue(), quads.GetAsNumber(i + 6).FloatValue() };
                var ys = new[] { quads.GetAsNumber(i + 1).FloatValue(), quads.GetAsNumber(i + 3).FloatValue(), quads.GetAsNumber(i + 5).FloatValue(), quads.GetAsNumber(i + 7).FloatValue() };
                rects.Add(geometry.UserRectToDisplay(xs.Min(), ys.Min(), xs.Max(), ys.Max()));
            }
            return EncodeTextHighlight(rects);
        }

        private static string ReadInkFormat(PdfAnnotation annot, PdfPageGeometry geometry)
        {
            var strokes = annot.GetPdfObject().GetAsArray(PdfName.InkList);
            if (strokes == null || strokes.Size() == 0) return "";
            // A Pencil stroke made by this app has one point list. Other producers may have several;
            // keep the first stroke editable and leave their original appearance intact until altered.
            var points = strokes.GetAsArray(0);
            if (points == null || points.Size() < 4) return "";
            var display = new List<(double U, double V)>();
            for (int index = 0; index + 1 < points.Size(); index += 2)
            {
                double x = points.GetAsNumber(index)?.DoubleValue() ?? 0;
                double y = points.GetAsNumber(index + 1)?.DoubleValue() ?? 0;
                var (u1, v1, _, _) = geometry.UserRectToDisplay(x, y, x, y);
                display.Add((u1, v1));
            }
            return EncodeInkPoints(display);
        }

        private static QuickAnnotationKind? KindOf(PdfAnnotation annot)
        {
            var subtype = annot.GetSubtype();
            if (annot.GetPdfObject().ContainsKey(ShapeKey)) return QuickAnnotationKind.Shape;
            if (PdfName.FreeText.Equals(subtype))
                return annot.GetPdfObject().GetAsString(FormatKey)?.GetValue().StartsWith("C|", StringComparison.Ordinal) == true
                    ? QuickAnnotationKind.Callout : QuickAnnotationKind.Typewriter;
            if (PdfName.Text.Equals(subtype)) return annot.GetPdfObject().Get(PdfName.IRT) != null ? QuickAnnotationKind.Reply : QuickAnnotationKind.Comment;
            if (PdfName.Highlight.Equals(subtype)) return QuickAnnotationKind.Highlight;
            if (PdfName.Underline.Equals(subtype)) return QuickAnnotationKind.Underline;
            if (PdfName.StrikeOut.Equals(subtype)) return QuickAnnotationKind.StrikeOut;
            if (PdfName.Squiggly.Equals(subtype)) return QuickAnnotationKind.Squiggly;
            if (PdfName.Ink.Equals(subtype)) return QuickAnnotationKind.Ink;
            if (PdfName.Stamp.Equals(subtype)) return QuickAnnotationKind.Stamp;
            return null;
        }

        private static readonly Dictionary<string, PdfFont> _measureFonts = new();

        /// <summary>Typewriter / Note placed at (U1, V1): the rectangle it will have once written — selection, moving and hit test work
        /// before Save. Same metrics as <see cref="AddTypewriter"/> / <see cref="AddComment"/>.</summary>
        public static QuickAnnotationSpec WithMeasuredSize(QuickAnnotationSpec spec, PdfPageGeometry geometry)
        {
            double width, height;
            switch (spec.Kind)
            {
                case QuickAnnotationKind.Typewriter:
                case QuickAnnotationKind.Callout:
                {
                    var format = TextFormat.Decode(spec.Kind == QuickAnnotationKind.Callout ? DecodeCallout(spec.Format).TextFormat : spec.Format);
                    float size = (float)format.Size;
                    lock (_measureFonts)
                    {
                        string key = format.Family + format.Bold + format.Italic;
                        if (!_measureFonts.TryGetValue(key, out var font)) _measureFonts[key] = font = CreateFormatFont(format);
                        if (spec.Kind == QuickAnnotationKind.Typewriter)
                        {
                            var layout = LayoutTypewriter(spec.Text, font, format);
                            (width, height) = (layout.Width, layout.Height);
                        }
                        else
                        {
                            string[] lines = SplitLines(spec.Text);
                            width = Math.Max(100f, lines.Max(l => font.GetWidth(l, size)) + 2 * TypewriterPadding);
                            height = lines.Length * size * TypewriterLineHeight + 2 * TypewriterPadding;
                        }
                    }
                    break;
                }
                case QuickAnnotationKind.Comment:
                    width = height = CommentIconSize;
                    break;
                default:
                    return spec;
            }
            return spec with { U2 = spec.U1 + width / geometry.DisplayWidth, V2 = spec.V1 + height / geometry.DisplayHeight };
        }

        /// <summary>Font nhúng dùng lại trong 1 document (PdfFont gắn với document của nó).</summary>
        public sealed class FontSet
        {
            private readonly Dictionary<string, PdfFont> _fonts = new();
            internal PdfFont Unicode => Get("unicode", CreateUnicodeFont);
            internal PdfFont For(TextFormat format) => Get(format.Family + format.Bold + format.Italic, () => CreateFormatFont(format));
            private PdfFont Get(string key, Func<PdfFont> create) => _fonts.TryGetValue(key, out var f) ? f : _fonts[key] = create();
        }

        /// <summary>Ghi các thay đổi đang chờ vào file (Save). Chú thích lấy từ file mà chỉ bị dời chỗ / đổi trạng thái Resolved thì giữ
        /// nguyên appearance gốc (chỉ dịch toạ độ), còn lại gỡ rồi tạo lại từ spec.</summary>
        public static void ApplyChanges(PdfDocument doc, IEnumerable<QuickAnnotationChange> changes) => ApplyChanges(doc, changes, null);

        /// <summary>Như trên; <paramref name="conflicts"/> (nếu có) nhận tên các chú thích mà người khác đã đổi NỘI DUNG trong file sau lúc ta mở (ta vẫn ghi đè lên — bản của họ còn trong lịch sử file).</summary>
        public static void ApplyChanges(PdfDocument doc, IEnumerable<QuickAnnotationChange> changes, List<string>? conflicts)
        {
            var fonts = new FontSet();
            foreach (var change in changes)
            {
                var page = doc.GetPage(change.PageNumber);
                if (change is { Remove: { } before, Add: { } after } && after.ObjectNumber > 0 && after.ObjectNumber == before.ObjectNumber)
                {
                    if (Find(page, before.Name) is { } target)
                    {
                        if (conflicts != null && after.Text != before.Text)
                        {
                            string current = target.GetContents()?.GetValue() ?? "";
                            if (current != before.Text && current != after.Text) conflicts.Add(before.Name);
                        }
                        if (Math.Abs(after.U1 - before.U1) > 1e-9 || Math.Abs(after.V1 - before.V1) > 1e-9)
                            Translate(page, target, before, after);
                        if (after.Resolved != before.Resolved)
                            PdfCommentService.SetResolved(target.GetPdfObject(), after.Resolved);
                        if (after.Text != before.Text)
                        {
                            target.SetContents(new PdfString(after.Text, PdfEncodings.UNICODE_BIG));
                            target.GetPdfObject().SetModified();
                        }
                    }
                    continue;
                }
                if (change.Remove != null) Remove(page, change.Remove.Name);
                if (change.Add != null) AddGenerated(doc, page, change.Add, fonts);
            }
        }

        /// <summary>Tạo chú thích từ spec (kèm appearance) trên <paramref name="page"/> — dùng cả cho file thật lẫn "tài liệu tí hon" vẽ appearance.</summary>
        public static PdfAnnotation? AddGenerated(PdfDocument doc, PdfPage page, QuickAnnotationSpec spec, FontSet fonts)
        {
            int before = page.GetAnnotations().Count;
            switch (spec.Kind)
            {
                case QuickAnnotationKind.Typewriter:
                {
                    var format = TextFormat.Decode(spec.Format);
                    AddTypewriter(doc, page, spec, fonts.For(format), format);
                    break;
                }
                case QuickAnnotationKind.Callout:
                    AddCallout(doc, page, spec, fonts.For(TextFormat.Decode(DecodeCallout(spec.Format).TextFormat)));
                    break;
                case QuickAnnotationKind.Comment:
                    AddComment(doc, page, spec);
                    break;
                case QuickAnnotationKind.Reply:
                    AddReply(doc, page, spec);
                    break;
                case QuickAnnotationKind.Highlight:
                    AddHighlight(doc, page, spec);
                    break;
                case QuickAnnotationKind.Underline:
                    AddQuadLineMarkup(doc, page, spec, strike: false);
                    break;
                case QuickAnnotationKind.StrikeOut:
                    AddQuadLineMarkup(doc, page, spec, strike: true);
                    break;
                case QuickAnnotationKind.Squiggly:
                    AddQuadLineMarkup(doc, page, spec, strike: false, squiggly: true);
                    break;
                case QuickAnnotationKind.Ink:
                    AddInk(doc, page, spec);
                    break;
                case QuickAnnotationKind.Shape:
                    AddShape(doc, page, spec);
                    break;
                case QuickAnnotationKind.Stamp:
                    AddStamp(doc, page, spec, fonts.Unicode);
                    break;
                default:
                    return null;
            }
            var annotations = page.GetAnnotations();
            if (spec.Resolved && annotations.Count > before) PdfCommentService.SetResolved(annotations[before].GetPdfObject(), true);
            return annotations.Count > before ? annotations[before] : null;
        }

        /// <summary>Chú thích theo tên: /NM, "#o{số}_{thế hệ}" (đối tượng gián tiếp không có /NM) hoặc "#i{chỉ số}".</summary>
        public static PdfAnnotation? Find(PdfPage page, string name)
        {
            var annotations = page.GetAnnotations();
            if (name.StartsWith("#o", StringComparison.Ordinal))
            {
                var parts = name[2..].Split('_');
                if (parts.Length == 2 && int.TryParse(parts[0], out int number) && int.TryParse(parts[1], out int generation))
                    return annotations.FirstOrDefault(a => a.GetPdfObject().GetIndirectReference() is { } r && r.GetObjNumber() == number && r.GetGenNumber() == generation);
                return null;
            }
            if (name.StartsWith("#i", StringComparison.Ordinal))
                return int.TryParse(name.AsSpan(2), out int index) && index >= 0 && index < annotations.Count && annotations[index].GetName() == null ? annotations[index] : null;
            return annotations.FirstOrDefault(a => a.GetName()?.ToUnicodeString() == name);
        }

        /// <summary>Dời 1 chú thích có sẵn trong file: mọi toạ độ của nó (Rect, QuadPoints, L, Vertices, InkList, CL, Rect của popup) cùng
        /// một vector — appearance (/AP) khớp theo /Rect nên không phải vẽ lại.</summary>
        private static void Translate(PdfPage page, PdfAnnotation annot, QuickAnnotationSpec before, QuickAnnotationSpec after)
        {
            var geometry = GetGeometry(page);
            var (x0, y0, _, _) = geometry.DisplayRectToUser(before.U1, before.V1, before.U1, before.V1);
            var (x1, y1, _, _) = geometry.DisplayRectToUser(after.U1, after.V1, after.U1, after.V1);
            float dx = (float)(x1 - x0), dy = (float)(y1 - y0);
            var obj = annot.GetPdfObject();

            void Shift(PdfArray? array)
            {
                if (array == null) return;
                for (int i = 0; i + 1 < array.Size(); i += 2)
                {
                    if (array.GetAsNumber(i) is { } x) array.Set(i, new PdfNumber(x.DoubleValue() + dx));
                    if (array.GetAsNumber(i + 1) is { } y) array.Set(i + 1, new PdfNumber(y.DoubleValue() + dy));
                }
            }
            foreach (var key in new[] { PdfName.Rect, PdfName.QuadPoints, PdfName.L, PdfName.Vertices, PdfName.CL, CalloutBoxKey })
                Shift(obj.GetAsArray(key));
            if (obj.GetAsArray(PdfName.InkList) is { } ink)
                for (int i = 0; i < ink.Size(); i++) Shift(ink.GetAsArray(i));
            if (obj.GetAsDictionary(PdfName.Popup) is { } popup)
            {
                Shift(popup.GetAsArray(PdfName.Rect));
                popup.SetModified();
            }
            obj.SetModified();
        }

        // ── Gỡ ────────────────────────────────────────────────────────────

        private static void Remove(PdfPage page, string name)
        {
            PdfAnnotation? target = Find(page, name);
            if (target == null) return;

            var targetObject = target.GetPdfObject();
            var popupObject = targetObject.GetAsDictionary(PdfName.Popup);
            page.RemoveAnnotation(target);
            foreach (var annot in page.GetAnnotations().ToList())
            {
                var obj = annot.GetPdfObject();
                if ((popupObject != null && obj.Equals(popupObject)) ||
                    (PdfName.Popup.Equals(annot.GetSubtype()) && targetObject.Equals(obj.GetAsDictionary(PdfName.Parent))))
                    page.RemoveAnnotation(annot);
            }
        }

        // ── Typewriter (FreeText không viền, chữ hiện thẳng trên trang) ──

        /// <summary>The lines of a Typewriter box and its size in points: auto width follows the longest line, a fixed width (<see cref="TextFormat.Width"/>) wraps the text.</summary>
        internal static (string[] Lines, float Width, float Height) LayoutTypewriter(string text, PdfFont font, TextFormat format)
        {
            float size = (float)format.Size, lead = size * TypewriterLineHeight;
            string[] paragraphs = SplitLines(text);
            string[] lines;
            float width;
            if (format.Width > 0)
            {
                width = (float)Math.Max(40, format.Width);
                lines = WrapLines(paragraphs, font, size, width - 2 * TypewriterPadding);
            }
            else
            {
                lines = paragraphs;
                width = Math.Max(20f, lines.Max(l => font.GetWidth(l, size)) + 2 * TypewriterPadding);
            }
            return (lines, width, lines.Length * lead + 2 * TypewriterPadding);
        }

        private static void AddTypewriter(PdfDocument doc, PdfPage page, QuickAnnotationSpec spec, PdfFont font, TextFormat format)
        {
            var geometry = GetGeometry(page);
            float size = (float)format.Size;
            var color = ParseColor(format.Color);
            var rgb = color.GetColorValue();
            float lead = size * TypewriterLineHeight;
            var (lines, width, height) = LayoutTypewriter(spec.Text, font, format);

            var rect = DisplayBoxToUser(geometry, spec.U1, spec.V1, width, height);
            var annot = new PdfFreeTextAnnotation(rect, new PdfString(spec.Text, PdfEncodings.UNICODE_BIG));
            annot.SetDefaultAppearance(new PdfString(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{rgb[0]:0.###} {rgb[1]:0.###} {rgb[2]:0.###} rg /Helv {size:0.##} Tf")));
            annot.GetPdfObject().Put(FormatKey, new PdfString(format.Encode(), PdfEncodings.UNICODE_BIG));
            annot.GetPdfObject().Put(IntentKey, TypewriterIntent);
            annot.GetPdfObject().Put(PdfName.BS, new PdfDictionary(new Dictionary<PdfName, PdfObject> { [PdfName.W] = new PdfNumber(0) }));
            if (geometry.Rotation != 0) annot.GetPdfObject().Put(PdfName.Rotate, new PdfNumber(geometry.Rotation));
            StampCommon(annot, spec);

            // AP vẽ theo chiều trang HIỂN THỊ, /Matrix xoay ngược /Rotate để chữ đứng thẳng khi xem.
            var form = new PdfFormXObject(new Rectangle(0, 0, width, height));
            SetRotationMatrix(form, geometry.Rotation);
            var canvas = new PdfCanvas(form, doc);
            float inner = width - 2 * TypewriterPadding, firstBaseline = height - TypewriterPadding - size * 0.9f;
            float StartX(string line) => TypewriterPadding + (format.Align == 1 ? (inner - font.GetWidth(line, size)) / 2 : format.Align == 2 ? inner - font.GetWidth(line, size) : 0);
            canvas.BeginText().SetFontAndSize(font, size).SetFillColor(color);
            float previousX = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                float x = Math.Max(0, StartX(lines[i]));
                if (i == 0) canvas.MoveText(x, firstBaseline); else canvas.MoveText(x - previousX, -lead);
                previousX = x;
                canvas.ShowText(lines[i]);
            }
            canvas.EndText();
            if (format.Underline)
            {
                canvas.SetStrokeColor(color).SetLineWidth(Math.Max(0.5f, size * 0.06f));
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Length == 0) continue;
                    float x = Math.Max(0, StartX(lines[i])), y = firstBaseline - i * lead - size * 0.14f;
                    canvas.MoveTo(x, y).LineTo(x + font.GetWidth(lines[i], size), y).Stroke();
                }
            }
            canvas.Release();
            annot.SetNormalAppearance(form.GetPdfObject());
            page.AddAnnotation(annot);
        }

        // ── Stamp (dấu chữ có viền màu hoặc ảnh) ────────────────────────

        /// <summary>Cỡ dấu (point, theo chiều trang HIỂN THỊ) — to theo khổ giấy để dấu vẫn đọc được trên bản vẽ A1/A0.</summary>
        public static (double Width, double Height) StampSize(StampDefinition definition, string sub, double displayWidthPoints, PdfFont? font = null)
        {
            double scale = Math.Clamp(displayWidthPoints / 842.0, 1.0, 4.0);
            if (definition.IsImage)
            {
                double aspect = 1.0;
                try
                {
                    var image = iText.IO.Image.ImageDataFactory.Create(definition.ImagePath);
                    if (image.GetHeight() > 0) aspect = image.GetWidth() / image.GetHeight();
                }
                catch { }
                double h = 64 * scale;
                return (h * aspect, h);
            }
            font ??= CreateUnicodeFont();
            double size = 16 * scale, subSize = 9 * scale, pad = 8 * scale;
            double width = Math.Max(font.GetWidth(definition.Text, (float)size) * 1.08, sub.Length > 0 ? font.GetWidth(sub, (float)subSize) : 0) + 2 * pad;
            double height = size * 1.25 + (sub.Length > 0 ? subSize * 1.5 : 0) + 2 * pad;
            return (width, height);
        }

        private static void AddStamp(PdfDocument doc, PdfPage page, QuickAnnotationSpec spec, PdfFont font)
        {
            var geometry = GetGeometry(page);
            var (definition, opacity) = StampDefinition.Decode(spec.Text);
            string sub = definition.Sub;
            var (width, height) = StampSize(definition, sub, geometry.DisplayWidth, font);
            var rect = DisplayBoxToUser(geometry, spec.U1, spec.V1, width, height);

            var annot = new PdfStampAnnotation(rect);
            annot.SetStampName(new PdfName(definition.IsImage ? "Image" : "Custom"));
            annot.SetContents(new PdfString(definition.IsImage ? "Stamp" : definition.Text, PdfEncodings.UNICODE_BIG));
            StampCommon(annot, spec);

            var form = new PdfFormXObject(new Rectangle(0, 0, (float)width, (float)height));
            SetRotationMatrix(form, geometry.Rotation);
            var canvas = new PdfCanvas(form, doc);
            canvas.SaveState().SetExtGState(new PdfExtGState().SetFillOpacity(opacity / 100f).SetStrokeOpacity(opacity / 100f));
            if (definition.IsImage)
            {
                try
                {
                    canvas.AddXObjectFittedIntoRectangle(new PdfImageXObject(iText.IO.Image.ImageDataFactory.Create(definition.ImagePath)), new Rectangle(0, 0, (float)width, (float)height));
                }
                catch { /* ảnh không đọc được: dấu trống */ }
            }
            else
            {
                double scale = Math.Clamp(geometry.DisplayWidth / 842.0, 1.0, 4.0);
                float size = (float)(16 * scale), subSize = (float)(9 * scale), pad = (float)(8 * scale), line = (float)(3 * scale);
                var color = ParseColor(definition.Color);
                canvas.SetStrokeColor(color).SetFillColor(color).SetLineWidth(line);
                float inset = line / 2;
                canvas.RoundRectangle(inset, inset, (float)width - line, (float)height - line, 6 * (float)scale).Stroke();
                float textWidth = font.GetWidth(definition.Text, size);
                float textY = sub.Length > 0 ? pad + subSize * 1.5f : pad + (float)(height - 2 * pad - size) / 2;
                canvas.BeginText().SetFontAndSize(font, size).SetTextRenderingMode(PdfCanvasConstants.TextRenderingMode.FILL_STROKE).SetLineWidth(size * 0.02f)
                    .MoveText((width - textWidth) / 2, textY + size * 0.12f).ShowText(definition.Text).EndText();
                if (sub.Length > 0)
                {
                    float subWidth = font.GetWidth(sub, subSize);
                    canvas.BeginText().SetFontAndSize(font, subSize).SetTextRenderingMode(PdfCanvasConstants.TextRenderingMode.FILL).MoveText((width - subWidth) / 2, pad + subSize * 0.2f).ShowText(sub).EndText();
                }
            }
            canvas.RestoreState().Release();
            annot.SetNormalAppearance(form.GetPdfObject());
            page.AddAnnotation(annot);
        }

        private static Color ParseColor(string hex)
        {
            try
            {
                var c = System.Drawing.ColorTranslator.FromHtml(hex);
                return new DeviceRgb(c.R / 255f, c.G / 255f, c.B / 255f);
            }
            catch { return new DeviceRgb(0.75f, 0.22f, 0.17f); }
        }

        // ── Shapes (rectangle, cloud, oval, arrow, line) ────────────────

        private static void AddShape(PdfDocument doc, PdfPage page, QuickAnnotationSpec spec)
        {
            var geometry = GetGeometry(page);
            var style = ShapeStyle.Decode(spec.Format);
            double scale = ShapeStyle.PageScale(geometry.DisplayWidth);
            float lw = (float)(style.Width * scale);
            float width = (float)((spec.U2 - spec.U1) * geometry.DisplayWidth), height = (float)((spec.V2 - spec.V1) * geometry.DisplayHeight);
            var (left, bottom, right, top) = geometry.DisplayRectToUser(spec.U1, spec.V1, spec.U2, spec.V2);
            var rect = new Rectangle((float)left, (float)bottom, (float)(right - left), (float)(top - bottom));
            var color = ParseColor(style.Color);

            // Inner corners (line ends), in the AP's own coordinates (origin bottom-left, display orientation).
            float pad = style.IsLine ? (float)ShapeStyle.LinePad(lw) : 0;
            (float X, float Y)[] corners = { (pad, height - pad), (width - pad, height - pad), (pad, pad), (width - pad, pad) }; // TL, TR, BL, BR
            var start = corners[style.Corner];
            var end = corners[3 - style.Corner];

            PdfMarkupAnnotation annot;
            if (style.IsLine)
            {
                (double U, double V) ToDisplay((float X, float Y) p) => (spec.U1 + p.X / geometry.DisplayWidth, spec.V1 + (height - p.Y) / geometry.DisplayHeight);
                var (su, sv) = ToDisplay(start);
                var (eu, ev) = ToDisplay(end);
                var (sx, sy, _, _) = geometry.DisplayRectToUser(su, sv, su, sv);
                var (ex, ey, _, _) = geometry.DisplayRectToUser(eu, ev, eu, ev);
                var line = new PdfLineAnnotation(rect, new[] { (float)sx, (float)sy, (float)ex, (float)ey });
                if (style.Type == ShapeStyle.Arrow) line.SetLineEndingStyles(new PdfArray(new PdfObject[] { new PdfName("None"), new PdfName("OpenArrow") }));
                annot = line;
            }
            else if (style.Type == ShapeStyle.Oval) annot = new PdfCircleAnnotation(rect);
            else annot = new PdfSquareAnnotation(rect);
            annot.SetColor(color);
            annot.GetPdfObject().Put(PdfName.BS, new PdfDictionary(new Dictionary<PdfName, PdfObject> { [PdfName.W] = new PdfNumber(lw) }));
            annot.GetPdfObject().Put(ShapeKey, new PdfString(spec.Format, PdfEncodings.UNICODE_BIG));
            if (style.Type == ShapeStyle.Cloud)
                annot.GetPdfObject().Put(new PdfName("BE"), new PdfDictionary(new Dictionary<PdfName, PdfObject> { [PdfName.S] = new PdfName("C"), [new PdfName("I")] = new PdfNumber(2) }));
            StampCommon(annot, spec);

            var form = new PdfFormXObject(new Rectangle(0, 0, width, height));
            SetRotationMatrix(form, geometry.Rotation);
            var canvas = new PdfCanvas(form, doc);
            canvas.SetStrokeColor(color).SetFillColor(color).SetLineWidth(lw).SetLineCapStyle(PdfCanvasConstants.LineCapStyle.ROUND).SetLineJoinStyle(PdfCanvasConstants.LineJoinStyle.ROUND);
            switch (style.Type)
            {
                case ShapeStyle.Oval:
                    canvas.Ellipse(lw / 2, lw / 2, width - lw / 2, height - lw / 2).Stroke();
                    break;
                case ShapeStyle.Cloud:
                    DrawCloud(canvas, width, height, lw, scale);
                    break;
                case ShapeStyle.Line:
                    canvas.MoveTo(start.X, start.Y).LineTo(end.X, end.Y).Stroke();
                    break;
                case ShapeStyle.Arrow:
                {
                    double dx = end.X - start.X, dy = end.Y - start.Y, len = Math.Sqrt(dx * dx + dy * dy);
                    if (len < 1) break;
                    double ux = dx / len, uy = dy / len, head = Math.Min(Math.Max(10 * scale, 5 * lw), len * 0.6), half = head * 0.4;
                    double bx = end.X - ux * head, by = end.Y - uy * head;
                    canvas.MoveTo(start.X, start.Y).LineTo(bx + ux * 1, by + uy * 1).Stroke();
                    canvas.MoveTo(end.X, end.Y).LineTo(bx - uy * half, by + ux * half).LineTo(bx + uy * half, by - ux * half).ClosePath().Fill();
                    break;
                }
                default:
                    canvas.Rectangle(lw / 2, lw / 2, width - lw, height - lw).Stroke();
                    break;
            }
            canvas.Release();
            annot.SetNormalAppearance(form.GetPdfObject());
            page.AddAnnotation(annot);
        }

        /// <summary>Revision cloud: a row of outward semicircles along each edge of the (inset) rectangle.</summary>
        private static void DrawCloud(PdfCanvas canvas, float width, float height, float lw, double scale)
        {
            float r = (float)Math.Clamp(Math.Min(width, height) / 7, 3, 14 * scale);
            float x1 = r + lw / 2, y1 = r + lw / 2, x2 = width - r - lw / 2, y2 = height - r - lw / 2;
            if (x2 - x1 < 4 || y2 - y1 < 4) { canvas.Rectangle(lw / 2, lw / 2, width - lw, height - lw).Stroke(); return; }

            void Edge(float from, float to, Action<float, float> bump)
            {
                float length = to - from;
                int n = Math.Max(1, (int)Math.Round(length / (2 * r)));
                float step = length / n;
                for (int i = 0; i < n; i++) bump(from + step * i + step / 2, step / 2);
            }
            Edge(x1, x2, (c, rr) => canvas.Arc(c - rr, y2 - rr, c + rr, y2 + rr, 0, 180));       // top: bulge up
            Edge(y1, y2, (c, rr) => canvas.Arc(x1 - rr, c - rr, x1 + rr, c + rr, 90, 180));      // left: bulge left (walking downwards)
            Edge(x1, x2, (c, rr) => canvas.Arc(c - rr, y1 - rr, c + rr, y1 + rr, 180, 180));     // bottom: bulge down
            Edge(y1, y2, (c, rr) => canvas.Arc(x2 - rr, c - rr, x2 + rr, c + rr, -90, 180));     // right: bulge right
            canvas.Stroke();
        }

        // ── Comment (sticky note: chỉ hiện icon, nội dung trong popup) ──

        private static PdfTextAnnotation AddComment(PdfDocument doc, PdfPage page, QuickAnnotationSpec spec)
        {
            var geometry = GetGeometry(page);
            var rect = DisplayBoxToUser(geometry, spec.U1, spec.V1, CommentIconSize, CommentIconSize);
            var annot = new PdfTextAnnotation(rect);
            annot.SetContents(new PdfString(spec.Text, PdfEncodings.UNICODE_BIG));
            annot.SetIconName(new PdfName("Comment"));
            annot.SetOpen(false);
            annot.SetColor(new DeviceRgb(1f, 0.82f, 0.2f));
            StampCommon(annot, spec);

            var form = new PdfFormXObject(new Rectangle(0, 0, CommentIconSize, CommentIconSize));
            SetRotationMatrix(form, geometry.Rotation);
            var canvas = new PdfCanvas(form, doc);
            float s = CommentIconSize;
            canvas.SetLineWidth(1).SetStrokeColor(new DeviceRgb(0.45f, 0.33f, 0f)).SetFillColor(new DeviceRgb(1f, 0.82f, 0.2f))
                .MoveTo(2, 6).LineTo(2, s - 2).LineTo(s - 2, s - 2).LineTo(s - 2, 6).LineTo(9, 6).LineTo(5, 2).LineTo(6, 6)
                .ClosePath().FillStroke();
            canvas.SetLineWidth(1.2f);
            foreach (float y in new[] { s - 6.5f, s - 10f, s - 13.5f })
                canvas.MoveTo(5, y).LineTo(s - 5, y);
            canvas.Stroke().Release();
            annot.SetNormalAppearance(form.GetPdfObject());
            page.AddAnnotation(annot);

            // Popup để Foxit/Acrobat mở nội dung khi bấm icon — đặt bên phải icon trên trang hiển thị.
            double popupU = Math.Min(spec.U1 + CommentIconSize / geometry.DisplayWidth, 0.7);
            var popup = new PdfPopupAnnotation(DisplayBoxToUser(geometry, popupU, spec.V1, 200, 110));
            popup.SetOpen(false);
            popup.SetParent(annot);
            annot.SetPopup(popup);
            page.AddAnnotation(popup);
            return annot;
        }

        /// <summary>Reply kiểu Word: chỉ 1 icon Note trên trang cho cả luồng (gốc + mọi reply) — reply không vẽ icon
        /// riêng, chỉ tồn tại dưới dạng dữ liệu (/IRT liên kết chú thích gốc) để panel Comments/popup đọc lại.
        /// Đặt /F Hidden để Foxit/Acrobat mở file cũng không tự vẽ icon riêng cho nó.</summary>
        private static void AddReply(PdfDocument doc, PdfPage page, QuickAnnotationSpec spec)
        {
            var geometry = GetGeometry(page);
            var rect = DisplayBoxToUser(geometry, spec.U1, spec.V1, 1, 1);
            var annot = new PdfTextAnnotation(rect);
            annot.SetContents(new PdfString(spec.Text, PdfEncodings.UNICODE_BIG));
            StampCommon(annot, spec);
            annot.SetFlags(PdfAnnotation.PRINT | PdfAnnotation.HIDDEN); // sau StampCommon: nó tự đặt /F = PRINT, sẽ đè mất HIDDEN nếu đặt trước
            page.AddAnnotation(annot);

            string parentName = spec.Format.StartsWith("R|", StringComparison.Ordinal) ? spec.Format[2..] : "";
            if (Find(page, parentName) is { } parent)
            {
                annot.GetPdfObject().Put(PdfName.IRT, parent.GetPdfObject());
                annot.GetPdfObject().Put(PdfName.RT, new PdfName("R"));
                annot.GetPdfObject().SetModified();
            }
        }

        // ── Highlight (tô vàng bán trong suốt đúng vùng kéo) ──────────────

        private static void AddHighlight(PdfDocument doc, PdfPage page, QuickAnnotationSpec spec)
        {
            var geometry = GetGeometry(page);
            var (left, bottom, right, top) = geometry.DisplayRectToUser(spec.U1, spec.V1, spec.U2, spec.V2);
            var rect = new Rectangle((float)left, (float)bottom, (float)(right - left), (float)(top - bottom));
            // "Highlight text": one rectangle per text line, encoded as "T|u1,v1,u2,v2;…" (display coordinates).
            var parts = TextHighlightRects(spec.Format).Select(r => geometry.DisplayRectToUser(r.U1, r.V1, r.U2, r.V2)).ToList();
            if (parts.Count == 0) parts.Add((left, bottom, right, top));
            var quad = parts.SelectMany(p => new[] { (float)p.Left, (float)p.Top, (float)p.Right, (float)p.Top, (float)p.Left, (float)p.Bottom, (float)p.Right, (float)p.Bottom }).ToArray();
            var annot = PdfTextMarkupAnnotation.CreateHighLight(rect, quad);
            annot.SetColor(new DeviceRgb(1f, 0.92f, 0f));
            annot.SetOpacity(new PdfNumber(0.85));
            if (!string.IsNullOrEmpty(spec.Text)) annot.SetContents(new PdfString(spec.Text, PdfEncodings.UNICODE_BIG));
            StampCommon(annot, spec);

            // Multiply giống bút dạ quang: chữ đen bên dưới vẫn đen, nền trắng thành vàng.
            var form = new PdfFormXObject(new Rectangle(0, 0, rect.GetWidth(), rect.GetHeight()));
            var canvas = new PdfCanvas(form, doc);
            canvas.SaveState().SetExtGState(new PdfExtGState().SetBlendMode(PdfExtGState.BM_MULTIPLY))
                .SetFillColor(new DeviceRgb(1f, 0.92f, 0f));
            foreach (var p in parts)
                canvas.Rectangle((float)(p.Left - left), (float)(p.Bottom - bottom), (float)(p.Right - p.Left), (float)(p.Top - p.Bottom));
            canvas.Fill().RestoreState().Release();
            annot.SetNormalAppearance(form.GetPdfObject());
            page.AddAnnotation(annot);
        }

        /// <summary>Underline / strikethrough: a coloured line per text line (from the "T|" rects), no page-pixel blending needed.</summary>
        private static void AddQuadLineMarkup(PdfDocument doc, PdfPage page, QuickAnnotationSpec spec, bool strike, bool squiggly = false)
        {
            var geometry = GetGeometry(page);
            var (left, bottom, right, top) = geometry.DisplayRectToUser(spec.U1, spec.V1, spec.U2, spec.V2);
            var rect = new Rectangle((float)left, (float)bottom, (float)(right - left), (float)(top - bottom));
            var parts = TextHighlightRects(spec.Format).Select(r => geometry.DisplayRectToUser(r.U1, r.V1, r.U2, r.V2)).ToList();
            if (parts.Count == 0) parts.Add((left, bottom, right, top));
            var quad = parts.SelectMany(p => new[] { (float)p.Left, (float)p.Top, (float)p.Right, (float)p.Top, (float)p.Left, (float)p.Bottom, (float)p.Right, (float)p.Bottom }).ToArray();
            var color = ParseColor(spec.Color.Length > 0 ? spec.Color : "#ED1C24");
            PdfTextMarkupAnnotation annot = squiggly ? PdfTextMarkupAnnotation.CreateSquiggly(rect, quad) : strike ? PdfTextMarkupAnnotation.CreateStrikeout(rect, quad) : PdfTextMarkupAnnotation.CreateUnderline(rect, quad);
            annot.SetColor(color);
            if (!string.IsNullOrEmpty(spec.Text)) annot.SetContents(new PdfString(spec.Text, PdfEncodings.UNICODE_BIG));
            StampCommon(annot, spec);

            var form = new PdfFormXObject(new Rectangle(0, 0, rect.GetWidth(), rect.GetHeight()));
            var canvas = new PdfCanvas(form, doc);
            canvas.SetStrokeColor(color);
            foreach (var p in parts)
            {
                double h = p.Top - p.Bottom;
                double lw = Math.Clamp(h * 0.07, 0.6, 3.0);
                double y = strike ? p.Bottom - bottom + h * 0.5 : p.Bottom - bottom + h * 0.12;
                if (!squiggly)
                {
                    canvas.SetLineWidth((float)lw).MoveTo((float)(p.Left - left), (float)y).LineTo((float)(p.Right - left), (float)y).Stroke();
                    continue;
                }
                // A compact zig-zag matches the PDF Squiggly subtype and stays crisp at every zoom level.
                double x0 = p.Left - left, x1 = p.Right - left, step = Math.Clamp(h * 0.28, 2.5, 7), amplitude = Math.Clamp(h * 0.10, 0.8, 2.2);
                canvas.SetLineWidth((float)lw).MoveTo((float)x0, (float)y);
                bool up = true;
                for (double x = x0 + step; x < x1; x += step)
                {
                    canvas.LineTo((float)x, (float)(y + (up ? amplitude : -amplitude)));
                    up = !up;
                }
                canvas.LineTo((float)x1, (float)y).Stroke();
            }
            canvas.Release();
            annot.SetNormalAppearance(form.GetPdfObject());
            page.AddAnnotation(annot);
        }

        private static void AddCallout(PdfDocument doc, PdfPage page, QuickAnnotationSpec spec, PdfFont font)
        {
            var geometry = GetGeometry(page);
            var callout = DecodeCallout(spec.Format);
            var format = TextFormat.Decode(callout.TextFormat);
            float size = (float)format.Size, lead = size * TypewriterLineHeight;
            // Hộp là spec.U1..V2 THẬT (người dùng kéo-giãn bằng tay qua grip) chứ không tự co theo chữ mỗi lần vẽ
            // lại — vì vậy chữ phải tự xuống dòng (word-wrap) theo đúng bề ngang hộp, tràn quá cao thì bị cắt (clip),
            // giống hệt cách 1 callout thật trong Foxit hoạt động.
            float width = Math.Max(40f, (float)((spec.U2 - spec.U1) * geometry.DisplayWidth));
            float height = Math.Max(20f, (float)((spec.V2 - spec.V1) * geometry.DisplayHeight));
            string[] lines = WrapLines(SplitLines(spec.Text), font, size, Math.Max(4f, width - 2 * TypewriterPadding));

            // TOÀN BỘ hình học dưới đây tính bằng đơn vị HIỂN THỊ (trục X sang phải, trục Y XUỐNG DƯỚI — như
            // spec.U/V), CHỈ đổi sang user-space (trục Y lên trên, theo /Rotate trang) đúng 1 lần ở /Rect và /CL.
            // Trước đây box/tip được đổi sang user-space TRƯỚC rồi mới lấy min/max/kích thước — với trang bị xoay
            // 90°/270°, DisplayRectToUser hoán trục X/Y, nên "width,height" (đo theo hiển thị) không còn khớp với
            // bề ngang/dọc thật của hộp trong user-space nữa. Hệ quả: BBox của form dựng theo user-space (đã hoán
            // trục) trong khi nội dung lại vẽ theo width/height hiển thị (chưa hoán) → PDF tự co giãn lệch trục
            // cho khớp /Rect khi hiển thị, chữ/đường dẫn bị bóp méo thành vệt ngắn không đọc được — đã thấy tận
            // mắt trên file thật (trang xoay 90°), tái hiện được bằng Tests/Program.cs --inspect-file.
            double dw = geometry.DisplayWidth, dh = geometry.DisplayHeight;
            double boxDispLeft = spec.U1 * dw, boxDispTop = spec.V1 * dh;
            double boxDispRight = boxDispLeft + width, boxDispBottom = boxDispTop + height;
            double tipDispX = callout.TipU * dw, tipDispY = callout.TipV * dh;

            // Đường dẫn = 1 đoạn THẲNG duy nhất, không khúc gấp — đúng như callout thật vẽ trong Foxit (đã mở Foxit
            // PhantomPDF, vẽ thử 1 callout để so, xem Tests/... không có, chỉ quan sát tay): 1 đường chéo thẳng từ
            // điểm chỉ tới điểm GẦN NHẤT trên biên hộp (không phải luôn góc trên-trái cố định như bản cũ).
            double attachDispX = Math.Clamp(tipDispX, boxDispLeft, boxDispRight);
            double attachDispY = Math.Clamp(tipDispY, boxDispTop, boxDispBottom);

            // /Rect (và AP) phải phủ luôn cả điểm chỉ + đường dẫn, không chỉ riêng hộp chữ — nếu không đường dẫn
            // sẽ bị cắt ở đúng mép hộp khi vẽ.
            var style = callout.Style;
            bool hasBorder = style.Border != CalloutStyle.None, hasFill = style.Fill != CalloutStyle.None;
            float headLength = style.Arrow > 0 ? (float)Math.Max(7, 5 * style.LineWidth) : 0f;
            double unionDispLeft = Math.Min(boxDispLeft, tipDispX) - headLength, unionDispRight = Math.Max(boxDispRight, tipDispX) + headLength;
            double unionDispTop = Math.Min(boxDispTop, tipDispY) - headLength, unionDispBottom = Math.Max(boxDispBottom, tipDispY) + headLength;
            double unionDispWidth = unionDispRight - unionDispLeft, unionDispHeight = unionDispBottom - unionDispTop;

            var (rLeft, rBottom, rRight, rTop) = geometry.DisplayRectToUser(
                unionDispLeft / dw, unionDispTop / dh, unionDispRight / dw, unionDispBottom / dh);
            var rect = new Rectangle((float)rLeft, (float)rBottom, (float)(rRight - rLeft), (float)(rTop - rBottom));
            var (tipX, tipY, _, _) = geometry.DisplayRectToUser(callout.TipU, callout.TipV, callout.TipU, callout.TipV);
            var (attachX, attachY, _, _) = geometry.DisplayRectToUser(attachDispX / dw, attachDispY / dh, attachDispX / dw, attachDispY / dh);

            var annot = new PdfFreeTextAnnotation(rect, new PdfString(spec.Text, PdfEncodings.UNICODE_BIG));
            var color = ParseColor(format.Color);
            var rgb = color.GetColorValue();
            annot.SetDefaultAppearance(new PdfString(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{rgb[0]:0.###} {rgb[1]:0.###} {rgb[2]:0.###} rg /Helv {size:0.##} Tf")));
            annot.GetPdfObject().Put(FormatKey, new PdfString(spec.Format, PdfEncodings.UNICODE_BIG));
            // Cố tình KHÔNG đặt /IT = FreeTextCallout (khác Typewriter): PDFium coi đây là tín hiệu "tự vẽ lại
            // appearance của tôi" cho callout (có lẽ vì có /CL) và đè hẳn lên /AP mình đã vẽ. Loại Callout vẫn
            // nhận ra được khi đọc lại nhờ tiền tố "C|" sẵn có trong <see cref="FormatKey"/> (xem DecodeCallout).
            // /CL 4 số = 2 điểm = 1 đoạn thẳng (đúng dạng chuẩn PDF, không phải dạng "khớp gối" 3 điểm/6 số).
            annot.GetPdfObject().Put(PdfName.CL, new PdfArray(new PdfObject[]
            {
                new PdfNumber(tipX), new PdfNumber(tipY), new PdfNumber(attachX), new PdfNumber(attachY)
            }));
            var (bLeft, bBottom, bRight, bTop) = geometry.DisplayRectToUser(boxDispLeft / dw, boxDispTop / dh, boxDispRight / dw, boxDispBottom / dh);
            annot.GetPdfObject().Put(CalloutBoxKey, new PdfArray(new double[] { bLeft, bBottom, bRight, bTop }));
            annot.GetPdfObject().Put(PdfName.BS, new PdfDictionary(new Dictionary<PdfName, PdfObject> { [PdfName.W] = new PdfNumber(hasBorder ? style.BorderWidth : 0) }));
            // Viền + nền xanh nhạt kiểu Foxit — đo trực tiếp màu pixel trên 1 callout Foxit thật vẽ ra (RGB 117,157,184
            // viền / 235,243,245 nền), không phải đoán mắt thường.
            var lineColor = ParseColor(style.LineColor);
            var borderColor = hasBorder ? ParseColor(style.Border) : lineColor;
            var fillColor = hasFill ? ParseColor(style.Fill) : new DeviceRgb(1f, 1f, 1f);
            annot.GetPdfObject().Put(PdfName.C, new PdfArray(borderColor.GetColorValue()));
            if (hasFill) annot.GetPdfObject().Put(PdfName.IC, new PdfArray(fillColor.GetColorValue()));
            annot.GetPdfObject().Put(new PdfName("LE"), new PdfName(style.Arrow == 1 ? "OpenArrow" : style.Arrow == 2 ? "ClosedArrow" : "None"));
            StampCommon(annot, spec);

            // AP thật (tự vẽ đường dẫn + hộp viền/nền + chữ) thay vì để PDFium tự sinh appearance từ /CL, /C, /IC —
            // BBox và mọi toạ độ vẽ CÙNG một hệ đơn vị hiển thị (chưa xoay) như width/height ở trên; /Matrix
            // (SetRotationMatrix) lo phần xoay khi PDF tự khớp BBox vào /Rect, giống hệt cách AddTypewriter làm.
            var form = new PdfFormXObject(new Rectangle(0, 0, (float)unionDispWidth, (float)unionDispHeight));
            SetRotationMatrix(form, geometry.Rotation);
            var canvas = new PdfCanvas(form, doc);
            // Toạ độ cục bộ Y-lên (canvas PDF) từ toạ độ hiển thị Y-xuống: local Y = chiều cao khối - (dispY - đỉnh khối).
            float ToLocalX(double dispX) => (float)(dispX - unionDispLeft);
            float ToLocalY(double dispY) => (float)(unionDispHeight - (dispY - unionDispTop));
            float lox = ToLocalX(boxDispLeft), loy = ToLocalY(boxDispBottom); // góc dưới-trái của hộp, toạ độ cục bộ
            float ltx = ToLocalX(tipDispX), lty = ToLocalY(tipDispY);
            float lax = ToLocalX(attachDispX), lay = ToLocalY(attachDispY);

            // Leader line from the box edge to the tip; the arrow head sits at the tip and points at it.
            float lineWidth = (float)style.LineWidth;
            canvas.SaveState().SetStrokeColor(lineColor).SetFillColor(lineColor).SetLineWidth(lineWidth)
                .SetLineCapStyle(PdfCanvasConstants.LineCapStyle.ROUND).SetLineJoinStyle(PdfCanvasConstants.LineJoinStyle.ROUND);
            double ldx = ltx - lax, ldy = lty - lay, llen = Math.Sqrt(ldx * ldx + ldy * ldy);
            if (style.Arrow > 0 && llen > 1)
            {
                double ux = ldx / llen, uy = ldy / llen, head = Math.Min(headLength, llen * 0.8), half = head * 0.42;
                double bx = ltx - ux * head, by = lty - uy * head;
                if (style.Arrow == 2)
                {
                    canvas.MoveTo(lax, lay).LineTo(bx + ux, by + uy).Stroke();
                    canvas.MoveTo(ltx, lty).LineTo(bx - uy * half, by + ux * half).LineTo(bx + uy * half, by - ux * half).ClosePath().Fill();
                }
                else
                {
                    canvas.MoveTo(lax, lay).LineTo(ltx, lty).Stroke();
                    canvas.MoveTo(bx - uy * half, by + ux * half).LineTo(ltx, lty).LineTo(bx + uy * half, by - ux * half).Stroke();
                }
            }
            else canvas.MoveTo(ltx, lty).LineTo(lax, lay).Stroke();
            canvas.RestoreState();
            canvas.SaveState().SetFillColor(fillColor).SetStrokeColor(borderColor).SetLineWidth(hasBorder ? (float)style.BorderWidth : 0);
            canvas.Rectangle(lox, loy, width, height);
            if (hasFill && hasBorder) canvas.FillStroke(); else if (hasFill) canvas.Fill(); else if (hasBorder) canvas.Stroke(); else canvas.EndPath();
            canvas.RestoreState();
            canvas.SaveState().Rectangle(lox, loy, width, height).Clip().EndPath()
                .BeginText().SetFontAndSize(font, size).SetFillColor(color)
                .MoveText(lox + TypewriterPadding, loy + height - TypewriterPadding - size * 0.9f);
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) canvas.MoveText(0, -lead);
                canvas.ShowText(lines[i]);
            }
            canvas.EndText().RestoreState().Release();
            annot.SetNormalAppearance(form.GetPdfObject());

            page.AddAnnotation(annot);
        }

        /// <summary>Xuống dòng kiểu "tham lam" (word-wrap) từng đoạn theo bề ngang chữ thật (font.GetWidth) —
        /// một từ dài hơn cả hộp thì cứ để tràn 1 dòng riêng, không cắt giữa từ.</summary>
        private static string[] WrapLines(string[] paragraphs, PdfFont font, float size, float maxWidth)
        {
            var result = new List<string>();
            foreach (string paragraph in paragraphs)
            {
                string[] words = paragraph.Split(' ');
                if (words.Length == 0 || (words.Length == 1 && words[0].Length == 0)) { result.Add(""); continue; }
                string line = "";
                foreach (string word in words)
                {
                    string candidate = line.Length == 0 ? word : line + " " + word;
                    if (line.Length > 0 && font.GetWidth(candidate, size) > maxWidth)
                    {
                        result.Add(line);
                        line = word;
                    }
                    else line = candidate;
                }
                result.Add(line);
            }
            return result.ToArray();
        }

        /// <summary>Writes a standard /Ink annotation, not a flattened image. The point list remains usable by
        /// Acrobat/Foxit, while the explicit appearance makes it render consistently in PDFium.</summary>
        private static void AddInk(PdfDocument doc, PdfPage page, QuickAnnotationSpec spec)
        {
            var display = InkPoints(spec.Format);
            if (display.Count < 2) return;
            var geometry = GetGeometry(page);
            var user = display.Select(p => geometry.DisplayRectToUser(p.U, p.V, p.U, p.V)).ToList();
            var (left, bottom, right, top) = geometry.DisplayRectToUser(spec.U1, spec.V1, spec.U2, spec.V2);
            var rect = new Rectangle((float)left, (float)bottom, (float)(right - left), (float)(top - bottom));
            var inkPoints = new PdfArray();
            foreach (var point in user)
            {
                inkPoints.Add(new PdfNumber(point.Left));
                inkPoints.Add(new PdfNumber(point.Bottom));
            }
            var inkList = new PdfArray();
            inkList.Add(inkPoints);
            var obj = new PdfDictionary();
            obj.Put(PdfName.Type, PdfName.Annot);
            obj.Put(PdfName.Subtype, PdfName.Ink);
            obj.Put(PdfName.Rect, new PdfArray(rect));
            obj.Put(PdfName.InkList, inkList);
            obj.Put(PdfName.NM, new PdfString(spec.Name));
            obj.Put(PdfName.T, new PdfString(spec.Author.Length > 0 ? spec.Author : Environment.UserName, PdfEncodings.UNICODE_BIG));
            obj.Put(PdfName.M, (spec.Date is { } date ? new PdfDate(date) : new PdfDate()).GetPdfObject());
            obj.Put(PdfName.CreationDate, new PdfDate().GetPdfObject());
            obj.Put(PdfName.F, new PdfNumber(PdfAnnotation.PRINT));
            var color = ParseColor(spec.Color.Length > 0 ? spec.Color : "#D74B31");
            var annot = (PdfInkAnnotation)PdfAnnotation.MakeAnnotation(obj);
            annot.SetColor(color);
            annot.SetOpacity(new PdfNumber(spec.Opacity <= 0 ? 1 : spec.Opacity));

            var form = new PdfFormXObject(new Rectangle(0, 0, rect.GetWidth(), rect.GetHeight()));
            var canvas = new PdfCanvas(form, doc).SetStrokeColor(color).SetLineWidth(2f).SetLineCapStyle(PdfCanvasConstants.LineCapStyle.ROUND).SetLineJoinStyle(PdfCanvasConstants.LineJoinStyle.ROUND);
            canvas.MoveTo((float)(user[0].Left - left), (float)(user[0].Bottom - bottom));
            for (int index = 1; index < user.Count; index++)
                canvas.LineTo((float)(user[index].Left - left), (float)(user[index].Bottom - bottom));
            canvas.Stroke().Release();
            annot.SetNormalAppearance(form.GetPdfObject());
            page.AddAnnotation(annot);
        }

        /// <summary>Text-highlight rectangles (display coordinates) from a spec's Format; empty for an area highlight.</summary>
        internal static System.Collections.Generic.List<(double U1, double V1, double U2, double V2)> TextHighlightRects(string format)
        {
            var list = new System.Collections.Generic.List<(double, double, double, double)>();
            if (!format.StartsWith("T|", StringComparison.Ordinal)) return list;
            foreach (string part in format.Substring(2).Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var v = part.Split(',');
                if (v.Length == 4 && v.All(x => double.TryParse(x, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)))
                {
                    double P(int i) => double.Parse(v[i], System.Globalization.CultureInfo.InvariantCulture);
                    list.Add((P(0), P(1), P(2), P(3)));
                }
            }
            return list;
        }

        internal static List<(double U, double V)> InkPoints(string format)
        {
            var points = new List<(double, double)>();
            if (!format.StartsWith("I|", StringComparison.Ordinal)) return points;
            foreach (string part in format[2..].Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split(',');
                if (pair.Length != 2 || !double.TryParse(pair[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double u) ||
                    !double.TryParse(pair[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)) continue;
                points.Add((u, v));
            }
            return points;
        }

        internal static string EncodeInkPoints(IEnumerable<(double U, double V)> points)
            => "I|" + string.Join(";", points.Select(p => p.U.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture) + "," + p.V.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture)));

        internal static string EncodeCallout(double tipU, double tipV, string textFormat, CalloutStyle? style = null)
        {
            // "C|u,v[;style]|textFormat" - the style segment is left out for the legacy look so old files round-trip byte for byte.
            string styleText = style == null || style == CalloutStyle.Legacy ? "" : ";" + style.Encode();
            return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"C|{tipU:0.#####},{tipV:0.#####}{styleText}|{textFormat}");
        }

        internal static (double TipU, double TipV, string TextFormat, CalloutStyle Style) DecodeCallout(string format)
        {
            if (format.StartsWith("C|", StringComparison.Ordinal))
            {
                var parts = format[2..].Split('|', 2);
                var head = parts[0].Split(';', 2);
                var point = head[0].Split(',');
                if (point.Length == 2 && double.TryParse(point[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double u) &&
                    double.TryParse(point[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v))
                    return (u, v, parts.Length == 2 ? parts[1] : "", CalloutStyle.Decode(head.Length == 2 ? head[1] : null));
            }
            return (0, 0, "", CalloutStyle.Legacy);
        }

        public static string EncodeTextHighlight(System.Collections.Generic.IEnumerable<(double U1, double V1, double U2, double V2)> rects)
            => "T|" + string.Join(";", rects.Select(r => string.Join(",", new[] { r.U1, r.V1, r.U2, r.V2 }.Select(x => x.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture)))));

        // ── Dùng chung ──────────────────────────────────────────────────

        private static void StampCommon(PdfMarkupAnnotation annot, QuickAnnotationSpec spec)
        {
            annot.SetName(new PdfString(spec.Name));
            annot.SetTitle(new PdfString(spec.Author.Length > 0 ? spec.Author : Environment.UserName, PdfEncodings.UNICODE_BIG));
            annot.SetDate((spec.Date is { } date ? new PdfDate(date) : new PdfDate()).GetPdfObject());
            annot.SetCreationDate(new PdfDate().GetPdfObject());
            annot.SetFlags(PdfAnnotation.PRINT);
        }

        /// <summary>Hộp (width × height point) có góc trên-trái tại (u, v) trên trang HIỂN THỊ → Rect user space.</summary>
        private static Rectangle DisplayBoxToUser(PdfPageGeometry geometry, double u, double v, double width, double height)
        {
            double u2 = u + width / geometry.DisplayWidth;
            double v2 = v + height / geometry.DisplayHeight;
            var (left, bottom, right, top) = geometry.DisplayRectToUser(u, v, u2, v2);
            return new Rectangle((float)left, (float)bottom, (float)(right - left), (float)(top - bottom));
        }

        /// <summary>/Matrix của appearance: xoay NGƯỢC chiều kim đồng hồ đúng /Rotate của trang, để sau khi
        /// viewer xoay trang thuận chiều, nội dung trở lại đứng thẳng. BBox sau xoay được viewer tự khớp vào Rect.</summary>
        private static void SetRotationMatrix(PdfFormXObject form, int rotation)
        {
            if (rotation == 0) return;
            double rad = rotation * Math.PI / 180;
            float cos = (float)Math.Round(Math.Cos(rad)), sin = (float)Math.Round(Math.Sin(rad));
            form.Put(PdfName.Matrix, new PdfArray(new[] { cos, sin, -sin, cos, 0f, 0f }));
        }

        internal static string[] SplitLines(string text)
        {
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            return lines.Length == 0 ? new[] { "" } : lines;
        }

        private static PdfFont CreateUnicodeFont()
        {
            string? path = ResolveFontPath();
            if (path != null)
            {
                try { return PdfFontFactory.CreateFont(path, PdfEncodings.IDENTITY_H, PdfFontFactory.EmbeddingStrategy.PREFER_EMBEDDED); }
                catch { /* rơi xuống Helvetica */ }
            }
            return PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
        }

        private static string? ResolveFontPath()
        {
            if (FontPathOverride != null && File.Exists(FontPathOverride)) return FontPathOverride;
            string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(fonts))
                candidates.AddRange(new[] { "arial.ttf", "segoeui.ttf", "tahoma.ttf" }.Select(f => System.IO.Path.Combine(fonts, f)));
            candidates.Add("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf");
            return candidates.FirstOrDefault(File.Exists);
        }
    }
}
