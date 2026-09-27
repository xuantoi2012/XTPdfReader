using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace XTPdfMergeApp.Services
{
    /// <summary>Toán thuần cho tên file gợi ý khi lưu 1 window đã ghép từ nhiều file nguồn —
    /// không đụng WPF control, tách ra từ MainWindow để dễ đọc/kiểm tra độc lập.</summary>
    public static class FileNamingMath
    {
        public static string SuggestFileName(IEnumerable<string> pageSourcePaths)
        {
            var distinctSources = pageSourcePaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string baseName = Path.GetFileNameWithoutExtension(distinctSources[0]);
            return distinctSources.Count > 1 ? baseName + "_ghep.pdf" : baseName + ".pdf";
        }

        /// <summary>Tên gợi ý khi xuất vài trang: "Ten_trang_3.pdf", "Ten_trang_3-5.pdf" (liên tục)
        /// hoặc "Ten_trang_2,4,7.pdf"; nhiều hơn 5 trang rời thì chỉ ghi số lượng.</summary>
        public static string SuggestExtractFileName(string windowFileName, IEnumerable<int> pagePositions)
        {
            string baseName = Path.GetFileNameWithoutExtension(windowFileName);
            var positions = pagePositions.Where(p => p > 0).Distinct().OrderBy(p => p).ToList();
            if (positions.Count == 0) return baseName + "_trich.pdf";

            string range;
            if (positions.Count == 1) range = positions[0].ToString();
            else if (positions[^1] - positions[0] == positions.Count - 1) range = $"{positions[0]}-{positions[^1]}";
            else if (positions.Count <= 5) range = string.Join(",", positions);
            else return $"{baseName}_{positions.Count}_trang.pdf";
            return $"{baseName}_trang_{range}.pdf";
        }
    }
}
