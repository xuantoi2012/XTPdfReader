using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using iText.Kernel.Pdf;

namespace XTPdfMergeApp.Services
{
    /// <summary>One page and its paper size as the user sees it (the page's /Rotate applied): <see cref="Key"/> is "A1 landscape", "A3 portrait", … (<see cref="TitleBlockReader.SizeKey"/>).</summary>
    internal sealed record PaperPage(int Page, string Key, double WidthPt, double HeightPt);

    /// <summary>The pages of one paper size.</summary>
    internal sealed record PaperGroup(string Key, IReadOnlyList<PaperPage> Pages)
    {
        public int Count => Pages.Count;
        public string Label => $"{Key} · {Count} page{(Count == 1 ? "" : "s")}";
    }

    /// <summary>
    /// Paper size of every page of a file, read in one pass, and the pages grouped by size. The base for everything that depends on the paper: areas and stamp places that are
    /// kept per size (<see cref="AreaPresetStore"/>), and later printing in one go (choose the size, then the printer and tray by size).
    /// </summary>
    internal static class PaperSizeIndex
    {
        /// <summary>The size of the given pages (1 based; null = every page), in page order.</summary>
        public static Task<IReadOnlyList<PaperPage>> ReadAsync(string path, IReadOnlyList<int>? pages = null)
            => Task.Run<IReadOnlyList<PaperPage>>(() =>
            {
                var properties = new ReaderProperties();
                if (PdfThumbnailService.TryGetDocumentPassword(path) is { Length: > 0 } password) properties.SetPassword(Encoding.UTF8.GetBytes(password));
                using var doc = new PdfDocument(new PdfReader(path, properties));
                var result = new List<PaperPage>();
                int count = doc.GetNumberOfPages();
                foreach (int number in pages ?? Enumerable.Range(1, count).ToList())
                {
                    if (number < 1 || number > count) continue;
                    var page = doc.GetPage(number);
                    var box = page.GetMediaBox();
                    bool turned = ((page.GetRotation() % 360) + 360) % 360 % 180 != 0;
                    result.Add(new PaperPage(number, TitleBlockReader.SizeKey(page), turned ? box.GetHeight() : box.GetWidth(), turned ? box.GetWidth() : box.GetHeight()));
                }
                return result;
            });

        /// <summary>Pages of the same size together, the size with most pages first.</summary>
        public static IReadOnlyList<PaperGroup> Group(IEnumerable<PaperPage> pages)
            => pages.GroupBy(p => p.Key).Select(g => new PaperGroup(g.Key, g.OrderBy(p => p.Page).ToList()))
                .OrderByDescending(g => g.Count).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
    }

    /// <summary>A rectangle as fractions 0..1 of the page: an area to search, or the place and size of a stamp.</summary>
    internal sealed record AreaPreset(double U1, double V1, double U2, double V2)
    {
        public double Width => U2 - U1;
        public double Height => V2 - V1;
    }

    /// <summary>
    /// Areas and places kept per purpose and paper size (%LocalAppData%\XTPdfReader\area-presets.json), so an A1 sheet and an A3 sheet each remember their own and the next file of the
    /// same sizes needs no drawing again. Purposes: "find-text", "find-object", "stamp:{id of the stamp}".
    /// </summary>
    internal static class AreaPresetStore
    {
        private static string _filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XTPdfReader", "area-presets.json");
        private static Dictionary<string, AreaPreset>? _items;

        /// <summary>Where the areas are kept (tests point it elsewhere so a real user's areas are never touched).</summary>
        internal static string FilePath
        {
            get => _filePath;
            set { _filePath = value; _items = null; }
        }

        private static string Key(string purpose, string sizeKey) => purpose + "|" + sizeKey;

        public static AreaPreset? Get(string purpose, string sizeKey) => Load().TryGetValue(Key(purpose, sizeKey), out var preset) ? preset : null;

        public static void Set(string purpose, string sizeKey, AreaPreset? preset)
        {
            var items = Load();
            if (preset == null) items.Remove(Key(purpose, sizeKey)); else items[Key(purpose, sizeKey)] = preset;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, FilePath, overwrite: true);
            }
            catch { /* cannot write: kept for this session only */ }
        }

        private static Dictionary<string, AreaPreset> Load()
        {
            if (_items != null) return _items;
            try { _items = JsonSerializer.Deserialize<Dictionary<string, AreaPreset>>(File.ReadAllText(FilePath)) ?? new(); }
            catch { _items = new(); }
            return _items;
        }
    }
}
