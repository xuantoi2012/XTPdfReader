using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using iText.IO.Image;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;

namespace XTPdfMergeApp.Services.Capture
{
    /// <summary>
    /// The store of screen captures. Every capture is a one-page PDF whose page is the picture, so text, notes, shapes and the other
    /// annotation tools of the Reader work on it, and it can be reopened and edited later. The folder holds the PDFs only; nothing else is indexed.
    /// </summary>
    internal static class CaptureLibrary
    {
        internal sealed record Entry(string Path, DateTime Created, long Length)
        {
            public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
        }

        public static string DefaultFolder => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XTPdfReader", "Captures");

        private static string? _folder;

        /// <summary>The folder in use (tests point it elsewhere).</summary>
        public static string Folder
        {
            get => _folder ?? DefaultFolder;
            set => _folder = value;
        }

        /// <summary>Page units per pixel: 72 / 96, so "actual size" in the Reader shows the capture at its real size on a 100 % screen.</summary>
        internal const double PointsPerPixel = 0.75;

        /// <summary>Writes the picture as a new one-page PDF and returns its path.</summary>
        public static string Save(BitmapSource image, DateTime? now = null)
        {
            var time = now ?? DateTime.Now;
            Directory.CreateDirectory(Folder);
            string stem = "Capture " + time.ToString("yyyy-MM-dd HH.mm.ss"), path = System.IO.Path.Combine(Folder, stem + ".pdf");
            for (int n = 2; File.Exists(path); n++) path = System.IO.Path.Combine(Folder, $"{stem} ({n}).pdf");

            float width = (float)(image.PixelWidth * PointsPerPixel), height = (float)(image.PixelHeight * PointsPerPixel);
            string temp = path + ".tmp";
            using (var document = new PdfDocument(new PdfWriter(temp)))
            {
                document.GetDocumentInfo().SetTitle(stem).SetCreator("PDF Reader Pro");
                var page = document.AddNewPage(new PageSize(width, height));
                new PdfCanvas(page).AddImageFittedIntoRectangle(ImageDataFactory.Create(CaptureImaging.EncodePng(image)), new Rectangle(0, 0, width, height), false);
            }
            File.Move(temp, path);
            return path;
        }

        /// <summary>Width of the capture in pixels, from its page size (the picture is the whole page).</summary>
        public static double PixelWidth(string path)
        {
            try
            {
                using var document = new PdfDocument(new PdfReader(path));
                return Math.Round(document.GetPage(1).GetPageSizeWithRotation().GetWidth() / PointsPerPixel);
            }
            catch { return 1200; }
        }

        /// <summary>Newest first.</summary>
        public static List<Entry> List()
        {
            if (!Directory.Exists(Folder)) return [];
            var result = new List<Entry>();
            try
            {
                foreach (string file in Directory.EnumerateFiles(Folder, "*.pdf"))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        result.Add(new Entry(file, info.CreationTime, info.Length));
                    }
                    catch { /* vanished meanwhile */ }
                }
            }
            catch { /* folder unreadable: show nothing */ }
            return result.OrderByDescending(e => e.Created).ToList();
        }

        /// <summary>Captures last changed at least <paramref name="olderThanDays"/> days ago, apart from <paramref name="inUse"/> (open in a tab).</summary>
        public static List<PrintedFilesService.Candidate> FindOlderThan(int olderThanDays, IEnumerable<string> inUse, DateTime? now = null)
        {
            var cutoff = (now ?? DateTime.Now).AddDays(-Math.Max(0, olderThanDays));
            var used = new HashSet<string>(inUse.Select(p => { try { return System.IO.Path.GetFullPath(p); } catch { return ""; } }), StringComparer.OrdinalIgnoreCase);
            var result = new List<PrintedFilesService.Candidate>();
            foreach (var entry in List())
            {
                if (used.Contains(entry.Path)) continue;
                try
                {
                    if (File.GetLastWriteTime(entry.Path) > cutoff) continue;
                    result.Add(new PrintedFilesService.Candidate(entry.Path, entry.Length));
                }
                catch { /* vanished meanwhile */ }
            }
            return result;
        }
    }
}
