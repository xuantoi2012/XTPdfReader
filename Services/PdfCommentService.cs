using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;

namespace XTPdfMergeApp.Services
{
    /// <summary>1 chú thích trong panel Comments (từ <see cref="AnnotationStore"/>: file + thay đổi chưa lưu), kể cả do app khác tạo.</summary>
    public sealed record CommentInfo(string Path, int Page, string Name, QuickAnnotationKind Kind, string Author, DateTime? Date, string Text, bool Resolved);

    /// <summary>Trạng thái Open / Resolved của chú thích (/StateModel /Review, /State /Completed).</summary>
    public static class PdfCommentService
    {
        private static readonly PdfName StateModel = new("StateModel");
        private static readonly PdfName State = new("State");

        /// <summary>Đặt / bỏ trạng thái "Resolved" của 1 chú thích.</summary>
        public static void SetResolved(PdfDictionary obj, bool resolved)
        {
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
