using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XTCapture
{
    /// <summary>
    /// The Store: every capture is a folder holding the picture as taken (<c>original.png</c>), a small picture for the list (<c>thumb.png</c>) and
    /// <c>meta.json</c>. The picture is never changed; comments drawn on it later go beside it (<c>markup.json</c>), so they stay editable.
    /// </summary>
    internal static class CaptureStore
    {
        internal sealed record Entry(string Id, string Folder, DateTime Created, int Width, int Height, long Length)
        {
            public string OriginalPath => Path.Combine(Folder, OriginalFile);
            public string ThumbPath => Path.Combine(Folder, ThumbFile);
            public string MarkupPath => Path.Combine(Folder, MarkupFile);
        }

        internal const string OriginalFile = "original.png", ThumbFile = "thumb.png", MetaFile = "meta.json", MarkupFile = "markup.json";
        private const int ThumbSide = 360;

        public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XTCapture", "Store");

        private static string? _folder;

        /// <summary>The folder in use (tests point it elsewhere).</summary>
        public static string Folder
        {
            get => _folder ?? DefaultFolder;
            set => _folder = value;
        }

        private sealed record Meta(string Created, int Width, int Height, int Version = 1);

        /// <summary>Keeps the picture as a new entry and returns it.</summary>
        public static Entry Save(BitmapSource image, DateTime? now = null)
        {
            var time = now ?? DateTime.Now;
            Directory.CreateDirectory(Folder);
            string stem = "Capture " + time.ToString("yyyy-MM-dd HH.mm.ss"), folder = Path.Combine(Folder, stem);
            for (int n = 2; Directory.Exists(folder); n++) folder = Path.Combine(Folder, $"{stem} ({n})");
            string temp = folder + ".tmp";
            Directory.CreateDirectory(temp);
            try
            {
                File.WriteAllBytes(Path.Combine(temp, OriginalFile), CaptureImaging.EncodePng(image));
                File.WriteAllBytes(Path.Combine(temp, ThumbFile), CaptureImaging.EncodePng(Thumbnail(image)));
                File.WriteAllText(Path.Combine(temp, MetaFile), JsonSerializer.Serialize(new Meta(time.ToString("o"), image.PixelWidth, image.PixelHeight)));
                Directory.Move(temp, folder); // a folder without the .tmp suffix is always complete
            }
            catch
            {
                try { Directory.Delete(temp, true); } catch { /* leave it: the list ignores .tmp folders */ }
                throw;
            }
            return Read(folder)!;
        }

        /// <summary>The list picture: the capture scaled to fit <see cref="ThumbSide"/> (never enlarged); transparent areas stay transparent.</summary>
        internal static BitmapSource Thumbnail(BitmapSource image)
        {
            double scale = Math.Min(1.0, ThumbSide / (double)Math.Max(image.PixelWidth, image.PixelHeight));
            if (scale >= 1) return image;
            var scaled = new TransformedBitmap(image, new ScaleTransform(scale, scale));
            scaled.Freeze();
            return scaled;
        }

        /// <summary>Newest first.</summary>
        public static List<Entry> List()
        {
            if (!Directory.Exists(Folder)) return [];
            var result = new List<Entry>();
            try
            {
                foreach (string folder in Directory.EnumerateDirectories(Folder))
                {
                    if (folder.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Read(folder) is { } entry) result.Add(entry);
                }
            }
            catch { /* folder unreadable: show nothing */ }
            return result.OrderByDescending(e => e.Created).ToList();
        }

        private static Entry? Read(string folder)
        {
            try
            {
                string original = Path.Combine(folder, OriginalFile);
                if (!File.Exists(original)) return null;
                var created = Directory.GetCreationTime(folder);
                int width = 0, height = 0;
                try
                {
                    var meta = JsonSerializer.Deserialize<Meta>(File.ReadAllText(Path.Combine(folder, MetaFile)));
                    if (meta != null)
                    {
                        width = meta.Width;
                        height = meta.Height;
                        if (DateTime.TryParse(meta.Created, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)) created = parsed;
                    }
                }
                catch { /* an entry without readable meta still shows, with the file's time */ }
                long length = Directory.EnumerateFiles(folder).Sum(f => new FileInfo(f).Length);
                return new Entry(Path.GetFileName(folder), folder, created, width, height, length);
            }
            catch { return null; }
        }

        /// <summary>The picture as taken.</summary>
        public static BitmapSource LoadOriginal(Entry entry) => Load(entry.OriginalPath);

        public static BitmapSource LoadThumbnail(Entry entry) => File.Exists(entry.ThumbPath) ? Load(entry.ThumbPath) : Thumbnail(LoadOriginal(entry));

        private static BitmapSource Load(string path)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; // read now, so the file is not held open
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }

        /// <summary>Entries kept at least <paramref name="olderThanDays"/> days.</summary>
        public static List<Entry> FindOlderThan(int olderThanDays, DateTime? now = null)
        {
            var cutoff = (now ?? DateTime.Now).AddDays(-Math.Max(0, olderThanDays));
            return List().Where(e => e.Created <= cutoff).ToList();
        }

        /// <summary>Sends the entry to the Recycle Bin (recoverable). Returns false when it could not be removed.</summary>
        public static bool Delete(Entry entry)
        {
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(entry.Folder, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                return true;
            }
            catch { return false; }
        }

        public static string FormatSize(long bytes)
            => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB" : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";
    }
}
