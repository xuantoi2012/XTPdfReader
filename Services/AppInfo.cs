using System.Reflection;

namespace XTPdfMergeApp.Services
{
    /// <summary>Tên và phiên bản hiển thị của app (tiêu đề cửa sổ, trang Settings).</summary>
    internal static class AppInfo
    {
        public const string DisplayName = "PDF Reader Pro";

        public static string Version
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return v == null ? "" : $"{v.Major}.{v.Minor}.{v.Build}";
            }
        }
    }
}
