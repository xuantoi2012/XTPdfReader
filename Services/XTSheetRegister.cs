using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace XTPdfMergeApp.Services
{
    /// <summary>1 dòng của bảng kê bản vẽ.</summary>
    internal sealed record RegisterRow(int Page, XTSheetPageInfo Info, string Size);

    /// <summary>Bảng kê bản vẽ (drawing register) từ thông tin sheet trong PDF: CSV mở thẳng bằng Excel (UTF-8 có BOM, dấu ; hoặc , theo vùng — dùng dấu , và đặt trong ngoặc kép khi cần).</summary>
    internal static class XTSheetRegister
    {
        public static readonly string[] Columns = { "No.", "Sheet number", "Title", "Scale", "Group", "Subset", "Paper size", "Page", "DWG", "Layout" };

        public static string ToCsv(IReadOnlyList<RegisterRow> rows)
        {
            var sb = new StringBuilder();
            sb.Append('﻿').AppendLine(string.Join(",", Columns.Select(Quote)));
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                sb.AppendLine(string.Join(",", new[]
                {
                    (i + 1).ToString(), r.Info.No, r.Info.Title, r.Info.Scale, r.Info.Group, r.Info.Subset, r.Size, r.Page.ToString(), r.Info.Dwg, r.Info.Layout
                }.Select(Quote)));
            }
            return sb.ToString();
        }

        private static string Quote(string value)
        {
            value ??= "";
            // Excel coi ô bắt đầu bằng = + - @ là công thức: thêm dấu nháy đơn để giữ nguyên chữ (số hiệu "-01" vẫn hiện đúng).
            if (value.Length > 0 && "=+-@".Contains(value[0])) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
