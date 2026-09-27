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
    public enum QuickAnnotationKind { Typewriter, Comment, Highlight }

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
        public bool Contains(double u, double v) => u >= U1 && u <= U2 && v >= V1 && v <= V2;
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

        /// <summary>Font TrueType có đủ dấu tiếng Việt cho typewriter. null = tự dò font hệ thống.</summary>
        public static string? FontPathOverride { get; set; }

        public static PdfPageGeometry GetGeometry(PdfPage page)
        {
            Rectangle crop = page.GetCropBox();
            int rotation = PdfPageEditService.NormalizeRotation(page.GetRotation());
            return new PdfPageGeometry(crop.GetX(), crop.GetY(), crop.GetWidth(), crop.GetHeight(), rotation);
        }

        /// <summary>Annotation Typewriter/Comment/Highlight (kể cả do app khác tạo) trên 1 trang.</summary>
        public static IReadOnlyList<QuickAnnotationSpec> ReadAnnotations(string path, int pageNumber)
            => ReadPage(path, pageNumber).Annotations;

        /// <summary>Hình học trang + annotation của trang — 1 lần mở file cho lớp tương tác của Viewer.</summary>
        public static (PdfPageGeometry Geometry, IReadOnlyList<QuickAnnotationSpec> Annotations) ReadPage(string path, int pageNumber)
        {
            using var doc = new PdfDocument(new PdfReader(path));
            var page = doc.GetPage(pageNumber);
            var geometry = GetGeometry(page);
            return (geometry, ReadAnnotations(page, geometry, pageNumber));
        }

        private static IReadOnlyList<QuickAnnotationSpec> ReadAnnotations(PdfPage page, PdfPageGeometry geometry, int pageNumber)
        {
            var result = new List<QuickAnnotationSpec>();
            var annotations = page.GetAnnotations();
            for (int i = 0; i < annotations.Count; i++)
            {
                var annot = annotations[i];
                QuickAnnotationKind? kind = KindOf(annot);
                if (kind == null) continue;
                var rect = annot.GetRectangle()?.ToRectangle();
                if (rect == null) continue;
                var (u1, v1, u2, v2) = geometry.UserRectToDisplay(rect.GetLeft(), rect.GetBottom(), rect.GetRight(), rect.GetTop());
                string name = annot.GetName()?.ToUnicodeString() is { Length: > 0 } nm ? nm : "#" + i;
                result.Add(new QuickAnnotationSpec(name, kind.Value, pageNumber, u1, v1, u2, v2,
                    annot.GetContents()?.ToUnicodeString() ?? ""));
            }
            return result;
        }

        private static QuickAnnotationKind? KindOf(PdfAnnotation annot)
        {
            var subtype = annot.GetSubtype();
            if (PdfName.FreeText.Equals(subtype)) return QuickAnnotationKind.Typewriter;
            if (PdfName.Text.Equals(subtype)) return QuickAnnotationKind.Comment;
            if (PdfName.Highlight.Equals(subtype) || PdfName.Square.Equals(subtype)) return QuickAnnotationKind.Highlight;
            return null;
        }

        public static void ApplyChanges(PdfDocument doc, IEnumerable<QuickAnnotationChange> changes)
        {
            PdfFont? typewriterFont = null;
            foreach (var change in changes)
            {
                var page = doc.GetPage(change.PageNumber);
                if (change.Remove != null) Remove(page, change.Remove.Name);
                if (change.Add == null) continue;
                switch (change.Add.Kind)
                {
                    case QuickAnnotationKind.Typewriter:
                        typewriterFont ??= CreateUnicodeFont();
                        AddTypewriter(doc, page, change.Add, typewriterFont);
                        break;
                    case QuickAnnotationKind.Comment:
                        AddComment(doc, page, change.Add);
                        break;
                    case QuickAnnotationKind.Highlight:
                        AddHighlight(doc, page, change.Add);
                        break;
                }
            }
        }

        // ── Gỡ ────────────────────────────────────────────────────────────

        private static void Remove(PdfPage page, string name)
        {
            var annotations = page.GetAnnotations();
            PdfAnnotation? target = annotations.FirstOrDefault(a => a.GetName()?.ToUnicodeString() == name);
            if (target == null && name.StartsWith('#') && int.TryParse(name.AsSpan(1), out int index) &&
                index >= 0 && index < annotations.Count && annotations[index].GetName() == null)
                target = annotations[index];
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

        private static void AddTypewriter(PdfDocument doc, PdfPage page, QuickAnnotationSpec spec, PdfFont font)
        {
            var geometry = GetGeometry(page);
            string[] lines = SplitLines(spec.Text);
            float size = TypewriterFontSize;
            float lead = size * TypewriterLineHeight;
            float width = Math.Max(20f, lines.Max(l => font.GetWidth(l, size)) + 2 * TypewriterPadding);
            float height = lines.Length * lead + 2 * TypewriterPadding;

            var rect = DisplayBoxToUser(geometry, spec.U1, spec.V1, width, height);
            var annot = new PdfFreeTextAnnotation(rect, new PdfString(spec.Text, PdfEncodings.UNICODE_BIG));
            annot.SetDefaultAppearance(new PdfString($"0 g /Helv {size:0.##} Tf"));
            annot.GetPdfObject().Put(IntentKey, TypewriterIntent);
            annot.GetPdfObject().Put(PdfName.BS, new PdfDictionary(new Dictionary<PdfName, PdfObject> { [PdfName.W] = new PdfNumber(0) }));
            if (geometry.Rotation != 0) annot.GetPdfObject().Put(PdfName.Rotate, new PdfNumber(geometry.Rotation));
            StampCommon(annot, spec);

            // AP vẽ theo chiều trang HIỂN THỊ, /Matrix xoay ngược /Rotate để chữ đứng thẳng khi xem.
            var form = new PdfFormXObject(new Rectangle(0, 0, width, height));
            SetRotationMatrix(form, geometry.Rotation);
            var canvas = new PdfCanvas(form, doc);
            canvas.BeginText().SetFontAndSize(font, size).SetFillColor(ColorConstants.BLACK)
                .MoveText(TypewriterPadding, height - TypewriterPadding - size * 0.9);
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) canvas.MoveText(0, -lead);
                canvas.ShowText(lines[i]);
            }
            canvas.EndText().Release();
            annot.SetNormalAppearance(form.GetPdfObject());
            page.AddAnnotation(annot);
        }

        // ── Comment (sticky note: chỉ hiện icon, nội dung trong popup) ──

        private static void AddComment(PdfDocument doc, PdfPage page, QuickAnnotationSpec spec)
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
        }

        // ── Highlight (tô vàng bán trong suốt đúng vùng kéo) ──────────────

        private static void AddHighlight(PdfDocument doc, PdfPage page, QuickAnnotationSpec spec)
        {
            var geometry = GetGeometry(page);
            var (left, bottom, right, top) = geometry.DisplayRectToUser(spec.U1, spec.V1, spec.U2, spec.V2);
            var rect = new Rectangle((float)left, (float)bottom, (float)(right - left), (float)(top - bottom));
            float[] quad = { (float)left, (float)top, (float)right, (float)top, (float)left, (float)bottom, (float)right, (float)bottom };
            var annot = PdfTextMarkupAnnotation.CreateHighLight(rect, quad);
            annot.SetColor(new DeviceRgb(1f, 0.92f, 0f));
            if (!string.IsNullOrEmpty(spec.Text)) annot.SetContents(new PdfString(spec.Text, PdfEncodings.UNICODE_BIG));
            StampCommon(annot, spec);

            // Multiply giống bút dạ quang: chữ đen bên dưới vẫn đen, nền trắng thành vàng.
            var form = new PdfFormXObject(new Rectangle(0, 0, rect.GetWidth(), rect.GetHeight()));
            var canvas = new PdfCanvas(form, doc);
            canvas.SaveState().SetExtGState(new PdfExtGState().SetBlendMode(PdfExtGState.BM_MULTIPLY).SetFillOpacity(0.85f))
                .SetFillColor(new DeviceRgb(1f, 0.92f, 0f))
                .Rectangle(0, 0, rect.GetWidth(), rect.GetHeight()).Fill().RestoreState().Release();
            annot.SetNormalAppearance(form.GetPdfObject());
            page.AddAnnotation(annot);
        }

        // ── Dùng chung ──────────────────────────────────────────────────

        private static void StampCommon(PdfMarkupAnnotation annot, QuickAnnotationSpec spec)
        {
            annot.SetName(new PdfString(spec.Name));
            annot.SetTitle(new PdfString(Environment.UserName, PdfEncodings.UNICODE_BIG));
            annot.SetDate(new PdfDate().GetPdfObject());
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
