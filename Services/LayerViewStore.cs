using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace XTPdfMergeApp.Services
{
    /// <summary>
    /// 1 "View" layer đã lưu: tập TÊN layer hiện / ẩn. Lưu theo tên (không theo id OCG) để dùng lại được cho file khác và cho file đã
    /// ghép (layer cùng tên gộp thành 1). Layer không có trong View giữ trạng thái mặc định của file.
    /// </summary>
    public sealed record LayerView(string Name, IReadOnlyList<string> Visible, IReadOnlyList<string> Hidden);

    /// <summary>Danh sách View layer của người dùng, lưu ở %LocalAppData%\XTPdfReader\layer-views.json (dùng chung mọi file).</summary>
    public static class LayerViewStore
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XTPdfReader", "layer-views.json");

        private static List<LayerView>? _views;

        public static IReadOnlyList<LayerView> Views => _views ??= Load();

        private static List<LayerView> Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new List<LayerView>();
                var views = JsonSerializer.Deserialize<List<LayerView>>(File.ReadAllText(FilePath));
                return views?.Where(v => !string.IsNullOrWhiteSpace(v.Name)).ToList() ?? new List<LayerView>();
            }
            catch { return new List<LayerView>(); } // file hỏng: bắt đầu lại từ danh sách rỗng
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_views, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, FilePath, overwrite: true);
            }
            catch { /* không ghi được thì View chỉ sống trong phiên này */ }
        }

        /// <summary>Thêm View, hoặc thay View cùng tên.</summary>
        public static void Upsert(LayerView view)
        {
            var list = (List<LayerView>)Views;
            int index = list.FindIndex(v => string.Equals(v.Name, view.Name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) list[index] = view; else list.Add(view);
            Save();
        }

        public static void Remove(string name)
        {
            var list = (List<LayerView>)Views;
            if (list.RemoveAll(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)) > 0) Save();
        }

        public static void Rename(string oldName, string newName)
        {
            var list = (List<LayerView>)Views;
            int index = list.FindIndex(v => string.Equals(v.Name, oldName, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return;
            list[index] = list[index] with { Name = newName };
            Save();
        }

        public static bool Exists(string name)
            => Views.Any(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}
