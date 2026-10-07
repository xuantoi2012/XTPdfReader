using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace XTPdfMergeApp.Services
{
    /// <summary>One line of the comment list export.</summary>
    internal sealed record CommentExportRow(string File, string Sheet, string SheetTitle, int Page, string Type, string Author, string Date, string Status, string ReplyTo, string Text);

    /// <summary>Review list for drawing sets: every comment with the sheet number / title of its page (from /XTSheet when the PDF has it), who wrote it,
    /// when, open or resolved, and whom it answers. CSV that Excel opens directly (UTF-8 with BOM; cells that start with = + - @ are protected).</summary>
    internal static class CommentExport
    {
        public static readonly string[] Columns = { "File", "Sheet", "Sheet title", "Page", "Type", "Author", "Date", "Status", "Reply to", "Comment" };

        /// <param name="sheetOf">Sheet info of (file, page 1-based) or null when the page has none.</param>
        public static IReadOnlyList<CommentExportRow> Build(IReadOnlyList<CommentInfo> comments, Func<string, int, XTSheetPageInfo?> sheetOf)
        {
            var byName = comments.Where(c => c.Name.Length > 0).GroupBy(c => (c.Path, c.Name)).ToDictionary(g => g.Key, g => g.First());
            return comments.OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Page).ThenBy(c => c.ParentName.Length > 0 ? 1 : 0).ThenBy(c => c.Date)
                .Select(c =>
                {
                    var sheet = sheetOf(c.Path, c.Page);
                    string replyTo = c.ParentName.Length > 0 && byName.TryGetValue((c.Path, c.ParentName), out var parent) ? (parent.Author.Length > 0 ? parent.Author : "Unknown") : "";
                    return new CommentExportRow(Path.GetFileName(c.Path), sheet?.No ?? "", sheet?.Title ?? "", c.Page, c.Kind.ToString(), c.Author, c.Date?.ToString("yyyy-MM-dd") ?? "",
                        c.Resolved ? "Resolved" : "Open", replyTo, c.Text ?? "");
                }).ToList();
        }

        public static string ToCsv(IReadOnlyList<CommentExportRow> rows)
        {
            var sb = new StringBuilder();
            sb.Append('﻿').Append(string.Join(",", Columns.Select(XTSheetRegister.Quote))).Append("\r\n");
            foreach (var r in rows)
                sb.Append(string.Join(",", new[] { r.File, r.Sheet, r.SheetTitle, r.Page.ToString(), r.Type, r.Author, r.Date, r.Status, r.ReplyTo, r.Text }
                    .Select(v => XTSheetRegister.Quote(System.Text.RegularExpressions.Regex.Replace(v ?? "", "\r\n|\r|\n", " "))))).Append("\r\n");
            return sb.ToString();
        }
    }
}
