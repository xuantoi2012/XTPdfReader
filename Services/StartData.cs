using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace XTPdfMergeApp.Services
{
    /// <summary>1 file trong danh sách "Recent" của màn hình Start.</summary>
    public sealed record RecentFile(string Path, DateTime OpenedUtc, bool Pinned, int Pages, long Size);

    /// <summary>Trang và kiểu zoom đang xem của một file lúc đóng (mở lại thì về đúng chỗ, như Foxit nhớ trang đã mở).</summary>
    public sealed record ViewPosition(string Path, int Page, int ZoomMode, double Zoom, DateTime SavedUtc);

    /// <summary>1 workspace đã lưu: bộ file đang mở (thứ tự tab) và file đang xem.</summary>
    public sealed record WorkspaceEntry(string Name, List<string> Files, string? Active);

    /// <summary>Danh sách JSON nhỏ lưu ở %LocalAppData%\XTPdfReader (ghi qua file tạm; hỏng thì bắt đầu lại từ rỗng).</summary>
    internal sealed class JsonListStore<T>
    {
        private readonly string _path;
        private List<T>? _items;

        public JsonListStore(string fileName)
            => _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XTPdfReader", fileName);

        public List<T> Items => _items ??= Load();

        private List<T> Load()
        {
            try { return File.Exists(_path) ? JsonSerializer.Deserialize<List<T>>(File.ReadAllText(_path)) ?? new() : new(); }
            catch { return new(); }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(Items, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, _path, overwrite: true);
            }
            catch { /* không ghi được: danh sách chỉ sống trong phiên này */ }
        }
    }

    /// <summary>Recent files (có ghim). Chỉ ghi khi mở file thật; file tạm của app không vào đây.</summary>
    internal static class RecentFilesStore
    {
        private const int MaxUnpinned = 30;
        private static readonly JsonListStore<RecentFile> _store = new("recent.json");

        /// <summary>Mới mở nhất trước; file ghim luôn lên đầu.</summary>
        public static IReadOnlyList<RecentFile> Items
            => _store.Items.OrderByDescending(r => r.Pinned).ThenByDescending(r => r.OpenedUtc).ToList();

        public static void NoteOpened(string path, int pages)
        {
            long size = 0;
            try { size = new FileInfo(path).Length; } catch { }
            var list = _store.Items;
            int i = list.FindIndex(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
            bool pinned = i >= 0 && list[i].Pinned;
            var entry = new RecentFile(path, DateTime.UtcNow, pinned, pages, size);
            if (i >= 0) list[i] = entry; else list.Add(entry);
            var extra = list.Where(r => !r.Pinned).OrderByDescending(r => r.OpenedUtc).Skip(MaxUnpinned).ToList();
            foreach (var r in extra) list.Remove(r);
            _store.Save();
        }

        public static void TogglePin(string path)
        {
            var list = _store.Items;
            int i = list.FindIndex(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return;
            list[i] = list[i] with { Pinned = !list[i].Pinned };
            _store.Save();
        }

        public static void Remove(string path)
        {
            if (_store.Items.RemoveAll(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)) > 0) _store.Save();
        }

        /// <summary>Xoá các file không ghim.</summary>
        public static void ClearUnpinned()
        {
            if (_store.Items.RemoveAll(r => !r.Pinned) > 0) _store.Save();
        }
    }

    internal static class ViewPositionStore
    {
        private const int MaxEntries = 300;
        private static readonly JsonListStore<ViewPosition> _store = new("positions.json");

        public static ViewPosition? Find(string path)
            => _store.Items.FirstOrDefault(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));

        public static void Note(string path, int page, int zoomMode, double zoom)
        {
            var list = _store.Items;
            int i = list.FindIndex(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
            var entry = new ViewPosition(path, page, zoomMode, zoom, DateTime.UtcNow);
            if (i >= 0) { if (list[i] == entry with { SavedUtc = list[i].SavedUtc }) return; list[i] = entry; } else list.Add(entry);
            foreach (var old in list.OrderByDescending(p => p.SavedUtc).Skip(MaxEntries).ToList()) list.Remove(old);
            _store.Save();
        }
    }

    internal static class WorkspaceStore
    {
        private static readonly JsonListStore<WorkspaceEntry> _store = new("workspaces.json");

        public static IReadOnlyList<WorkspaceEntry> Items => _store.Items;

        public static bool Exists(string name) => _store.Items.Any(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));

        public static void Upsert(WorkspaceEntry entry)
        {
            var list = _store.Items;
            int i = list.FindIndex(w => string.Equals(w.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) list[i] = entry; else list.Add(entry);
            _store.Save();
        }

        public static void Remove(string name)
        {
            if (_store.Items.RemoveAll(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase)) > 0) _store.Save();
        }
    }
}
