using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;

namespace XTPdfMergeApp.Services
{
    /// <summary>
    /// Images of single annotations, drawn by the annotation layer over the page (the page itself is rendered without
    /// annotations). Each annotation is rendered by PDFium on a transparent bitmap from a tiny in-memory PDF whose page
    /// has the same boxes and rotation as the source page and holds only that annotation — a copy of the file's
    /// annotation (its own appearance, whatever app made it) or one generated from the spec (same code as Save).
    /// So the result looks exactly like the saved file, and moving an annotation is only drawing its image elsewhere.
    /// Images are kept per zoom level (√2 steps); a coarser one is drawn scaled until the sharp one is ready.
    /// </summary>
    internal static class AnnotationAppearance
    {
        /// <summary>Image and the page region it covers, relative to the annotation's top-left (display, 0..1 of the page).</summary>
        internal sealed record Image(BitmapSource Bitmap, double DU1, double DV1, double DU2, double DV2, long Bytes);

        private const int MinLevel = -8, MaxLevel = 9; // 2^(level/2) pixels per point: 0.06 … 22.6
        private const long BudgetBytes = 96L * 1024 * 1024;
        private const long MaxImagePixels = 6_000_000;
        private const int PaddingPixels = 2;

        private static readonly Dictionary<string, SortedDictionary<int, Image>> _images = new();
        private static readonly LinkedList<(string Key, int Level)> _lru = new();
        private static readonly Dictionary<(string, int), LinkedListNode<(string Key, int Level)>> _lruNodes = new();
        private static long _bytes;
        private static readonly HashSet<(string, int)> _inFlight = new();
        private static readonly Dictionary<(string Path, int Page), List<(QuickAnnotationSpec Spec, string Key, int Level)>> _queue = new();
        private static bool _flushScheduled;

        public static int LevelFor(double pixelsPerPoint)
            => Math.Clamp((int)Math.Ceiling(2 * Math.Log2(Math.Max(1e-3, pixelsPerPoint * 0.97))), MinLevel, MaxLevel);

        public static double PixelsPerPoint(int level) => Math.Pow(2, level / 2.0);

        /// <summary>Appearance identity: the file object (its own appearance) or everything the generated appearance depends on.</summary>
        public static string KeyOf(string path, QuickAnnotationSpec spec, PdfPageGeometry geometry)
        {
            if (spec.ObjectNumber > 0)
                return $"F|{AnnotationStore.Normalize(path)}|{AnnotationStore.Epoch(path)}|{spec.ObjectNumber}|{spec.Generation}";
            string identity = string.Join("|", spec.Kind, spec.Text, spec.Format, spec.Color, spec.Opacity.ToString("0.###"),
                ((spec.U2 - spec.U1) * geometry.DisplayWidth).ToString("0.##"), ((spec.V2 - spec.V1) * geometry.DisplayHeight).ToString("0.##"),
                geometry.Rotation, geometry.DisplayWidth.ToString("0.#"));
            return "G|" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(identity)));
        }

        /// <summary>
        /// Best image for drawing at <paramref name="pixelsPerPoint"/>: the smallest level at least that sharp, else the sharpest coarser one
        /// (and the sharp one is requested). null = nothing yet (requested; <see cref="AnnotationStore.Changed"/> fires when ready).
        /// </summary>
        public static Image? Get(string path, int page, QuickAnnotationSpec spec, PdfPageGeometry geometry, double pixelsPerPoint)
        {
            string key = KeyOf(path, spec, geometry);
            int wanted = LevelFor(pixelsPerPoint);
            Image? coarser = null, finer = null;
            int finerLevel = int.MaxValue;
            if (_images.TryGetValue(key, out var levels))
                foreach (var (level, image) in levels)
                {
                    if (level >= wanted && level < finerLevel) { finer = image; finerLevel = level; }
                    else if (level < wanted) coarser = image;
                }
            if (finer != null)
            {
                Touch(key, finerLevel);
                return finer;
            }
            Request(path, page, spec, key, wanted);
            return coarser;
        }

        private static void Touch(string key, int level)
        {
            if (!_lruNodes.TryGetValue((key, level), out var node)) return;
            _lru.Remove(node);
            _lru.AddLast(node);
        }

        private static void Request(string path, int page, QuickAnnotationSpec spec, string key, int level)
        {
            if (!_inFlight.Add((key, level))) return;
            var id = (AnnotationStore.Normalize(path), page);
            if (!_queue.TryGetValue(id, out var list)) _queue[id] = list = new();
            list.Add((spec, key, level));
            if (_flushScheduled) return;
            _flushScheduled = true;
            // Gather every request of this frame (all annotations of the visible pages) into one document per page.
            Application.Current.Dispatcher.BeginInvoke(Flush, System.Windows.Threading.DispatcherPriority.Background);
        }

        private static void Flush()
        {
            _flushScheduled = false;
            var batches = _queue.ToList();
            _queue.Clear();
            foreach (var (id, items) in batches) _ = RenderAsync(id.Path, id.Page, items);
        }

        private static async Task RenderAsync(string path, int page, List<(QuickAnnotationSpec Spec, string Key, int Level)> items)
        {
            List<(string Key, int Level, Image Image)> done = new();
            try
            {
                var reader = AnnotationStore.Reader(path);
                var built = await Task.Run(() => Build(reader, page, items.Select(i => i.Spec).ToList()));
                if (built != null)
                {
                    var jobs = new List<PdfThumbnailService.MemoryRenderJob>();
                    var regions = new List<(double U1, double V1, double U2, double V2, int FullW, int FullH, int X, int Y, double RefU, double RefV)>();
                    for (int i = 0; i < items.Count; i++)
                    {
                        var (spec, _, level) = items[i];
                        var r = built.Value.Regions[i];
                        var g = built.Value.Geometry;
                        double ppp = PixelsPerPoint(level);
                        // Big annotations at deep zoom: cap the image size (drawn slightly soft rather than using too much memory).
                        double areaPoints = Math.Max(1, (r.U2 - r.U1) * g.DisplayWidth * (r.V2 - r.V1) * g.DisplayHeight);
                        ppp = Math.Min(ppp, Math.Sqrt(MaxImagePixels / areaPoints));
                        int fullW = Math.Max(1, (int)Math.Round(g.DisplayWidth * ppp)), fullH = Math.Max(1, (int)Math.Round(g.DisplayHeight * ppp));
                        int x0 = Math.Max(0, (int)Math.Floor(r.U1 * fullW) - PaddingPixels), y0 = Math.Max(0, (int)Math.Floor(r.V1 * fullH) - PaddingPixels);
                        int x1 = Math.Min(fullW, (int)Math.Ceiling(r.U2 * fullW) + PaddingPixels), y1 = Math.Min(fullH, (int)Math.Ceiling(r.V2 * fullH) + PaddingPixels);
                        jobs.Add(new PdfThumbnailService.MemoryRenderJob(i, fullW, fullH, new Int32Rect(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0))));
                        regions.Add((r.U1, r.V1, r.U2, r.V2, fullW, fullH, x0, y0, r.RefU, r.RefV));
                    }
                    var bitmaps = await PdfThumbnailService.RenderMemoryPagesAsync(built.Value.Pdf, jobs);
                    for (int i = 0; i < items.Count; i++)
                    {
                        if (bitmaps[i] is not { } bitmap) continue;
                        var (spec, key, level) = items[i];
                        var reg = regions[i];
                        // Region relative to the annotation's own top-left as it was rendered (the file position for a moved file annotation).
                        double refU = reg.RefU, refV = reg.RefV; // a callout's /Rect also covers its arrow, but the spec (and the selection frame) is the text box
                        double u0 = (double)reg.X / reg.FullW, v0 = (double)reg.Y / reg.FullH;
                        double u1 = (double)(reg.X + bitmap.PixelWidth) / reg.FullW, v1 = (double)(reg.Y + bitmap.PixelHeight) / reg.FullH;
                        done.Add((key, level, new Image(bitmap, u0 - refU, v0 - refV, u1 - refU, v1 - refV, (long)bitmap.PixelWidth * bitmap.PixelHeight * 4)));
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AnnotationAppearance] " + ex.Message);
            }
            finally
            {
                foreach (var (_, key, level) in items) _inFlight.Remove((key, level));
            }
            foreach (var (key, level, image) in done) Store(key, level, image);
            if (done.Count > 0) AnnotationStore.RaiseChanged(path, page);
        }

        private static void Store(string key, int level, Image image)
        {
            if (!_images.TryGetValue(key, out var levels)) _images[key] = levels = new();
            if (levels.TryGetValue(level, out var old)) { _bytes -= old.Bytes; if (_lruNodes.Remove((key, level), out var n)) _lru.Remove(n); }
            levels[level] = image;
            _bytes += image.Bytes;
            _lruNodes[(key, level)] = _lru.AddLast((key, level));
            while (_bytes > BudgetBytes && _lru.First is { } first)
            {
                _lru.RemoveFirst();
                _lruNodes.Remove(first.Value);
                if (_images.TryGetValue(first.Value.Key, out var l) && l.Remove(first.Value.Level, out var evicted))
                {
                    _bytes -= evicted.Bytes;
                    if (l.Count == 0) _images.Remove(first.Value.Key);
                }
            }
        }

        /// <summary>Tiny PDF: page i = source page boxes/rotation + annotation i only. Regions = where each annotation sits (display, 0..1).</summary>
        private static (byte[] Pdf, PdfPageGeometry Geometry, List<(double U1, double V1, double U2, double V2, double RefU, double RefV)> Regions)? Build(
            AnnotationSourceReader reader, int pageNumber, IReadOnlyList<QuickAnnotationSpec> specs)
        {
            var regions = new List<(double, double, double, double, double, double)>();
            var output = new MemoryStream();
            PdfPageGeometry geometry;
            lock (reader.Sync)
            {
                var source = reader.Document();
                if (pageNumber < 1 || pageNumber > source.GetNumberOfPages()) return null;
                var sourcePage = source.GetPage(pageNumber);
                geometry = PdfQuickAnnotationService.GetGeometry(sourcePage);
                var excluded = new List<PdfName> { PdfName.P, PdfName.Popup, PdfName.Parent, PdfName.IRT };
                using var tiny = new PdfDocument(new PdfWriter(output));
                var fonts = new PdfQuickAnnotationService.FontSet();
                foreach (var spec in specs)
                {
                    var page = tiny.AddNewPage(new PageSize(sourcePage.GetMediaBox()));
                    page.SetMediaBox(sourcePage.GetMediaBox());
                    page.SetCropBox(sourcePage.GetCropBox());
                    page.SetRotation(sourcePage.GetRotation());
                    Rectangle? rect = null;
                    PdfArray? boxArray = null;
                    var calloutBoxKey = new PdfName("XTBox");
                    if (spec.ObjectNumber > 0)
                    {
                        var original = sourcePage.GetAnnotations().FirstOrDefault(a => a.GetPdfObject().GetIndirectReference() is { } r &&
                            r.GetObjNumber() == spec.ObjectNumber && r.GetGenNumber() == spec.Generation);
                        if (original != null)
                        {
                            var copy = (PdfDictionary)original.GetPdfObject().Clone(excluded).CopyTo(tiny);
                            page.GetPdfObject().Put(PdfName.Annots, new PdfArray(copy));
                            rect = original.GetRectangle()?.ToRectangle();
                            boxArray = original.GetPdfObject().GetAsArray(calloutBoxKey);
                        }
                    }
                    else
                    {
                        var generated = PdfQuickAnnotationService.AddGenerated(tiny, page, spec, fonts);
                        rect = generated?.GetRectangle()?.ToRectangle();
                        boxArray = generated?.GetPdfObject().GetAsArray(calloutBoxKey);
                    }
                    var region = rect == null ? (spec.U1, spec.V1, spec.U2, spec.V2)
                        : geometry.UserRectToDisplay(rect.GetLeft(), rect.GetBottom(), rect.GetRight(), rect.GetTop());
                    double refU = region.Item1, refV = region.Item2;
                    if (boxArray is { } b && b.Size() == 4 && b.GetAsNumber(0) is { } l && b.GetAsNumber(1) is { } bo && b.GetAsNumber(2) is { } r2 && b.GetAsNumber(3) is { } t)
                    {
                        var box = geometry.UserRectToDisplay(l.DoubleValue(), bo.DoubleValue(), r2.DoubleValue(), t.DoubleValue());
                        refU = box.Item1; refV = box.Item2;
                    }
                    regions.Add((region.Item1, region.Item2, region.Item3, region.Item4, refU, refV));
                }
            }
            return (output.ToArray(), geometry, regions);
        }
    }
}
