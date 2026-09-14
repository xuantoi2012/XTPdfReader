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
    }
}
