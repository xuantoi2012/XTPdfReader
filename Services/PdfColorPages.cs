using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XTPdfMergeApp.Services
{
    /// <summary>
    /// Which pages of a drawing set need color: the pages that really show color, whatever it is made of (a picture, colored text, a colored band or table). Each page is drawn small
    /// (260 px wide, the way the drawing set is read) and the share of the page whose pixels are colored (the three channels differ clearly) is measured. A color logo in the title block of
    /// every sheet covers about 0.1% of the page; a colored title block, a colored table or a picture covers several times that, so the dialog calls a page a color page from
    /// <see cref="AppSettings.ColorPageTenths"/> tenths of a percent upward. The share of every page is kept until the file changes, so changing the limit does not draw the file again.
    /// This is the quick pick: the print dialog lets the user add or remove pages.
    /// </summary>
    internal static class PdfColorPages
    {
        private const int ReadWidth = 260;
        private const int ChromaLimit = 30;

        private static readonly ConcurrentDictionary<string, (long Stamp, Dictionary<int, double> Share)> Cache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>For each page (1-based): the percent (0..100) of the page that is colored. Drawn in the background, a few pages at a time.</summary>
        public static async Task<Dictionary<int, double>> ShareAsync(string path, IProgress<(int Done, int Total)>? progress = null, CancellationToken token = default)
        {
            try
            {
                var info = new FileInfo(path);
                long stamp = info.LastWriteTimeUtc.Ticks ^ info.Length;
                if (Cache.TryGetValue(path, out var cached) && cached.Stamp == stamp) return cached.Share;
                var sizes = await PdfThumbnailService.GetPageSizesAsync(path);
                int count = sizes?.Length ?? 0;
                var result = new Dictionary<int, double>();
                int done = 0;
                string layers = PdfLayerStateStore.GetToken(path);
                using var gate = new SemaphoreSlim(4);
                var tasks = Enumerable.Range(0, count).Select(async index =>
                {
                    await gate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        var bitmap = await PdfThumbnailService.RenderPageAsync(path, index, ReadWidth, token, layerToken: layers, withAnnotations: true).ConfigureAwait(false);
                        double share = bitmap == null ? 0 : ColoredPercent(bitmap);
                        lock (result) result[index + 1] = share;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { lock (result) result[index + 1] = 0; }
                    finally
                    {
                        gate.Release();
                        progress?.Report((Interlocked.Increment(ref done), count));
                    }
                }).ToList();
                await Task.WhenAll(tasks).ConfigureAwait(false);
                Cache[path] = (stamp, result);
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch { return new Dictionary<int, double>(); }
        }

        /// <summary>The pages whose colored share is at least <paramref name="tenthsOfPercent"/> tenths of a percent.</summary>
        public static HashSet<int> PagesAbove(IReadOnlyDictionary<int, double> share, int tenthsOfPercent)
            => share.Where(f => f.Value * 10 >= tenthsOfPercent).Select(f => f.Key).ToHashSet();

        public static HashSet<int> Find(string path, int tenthsOfPercent) => PagesAbove(ShareAsync(path).GetAwaiter().GetResult(), tenthsOfPercent);

        /// <summary>The percent of the picture whose pixels are colored: the spread between the strongest and the weakest of red, green and blue is clear.</summary>
        internal static double ColoredPercent(BitmapSource source)
        {
            var converted = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int w = converted.PixelWidth, h = converted.PixelHeight, stride = w * 4;
            if (w <= 0 || h <= 0) return 0;
            var pixels = new byte[stride * h];
            converted.CopyPixels(pixels, stride, 0);
            long colored = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                if (max - min > ChromaLimit) colored++;
            }
            return colored * 100.0 / ((long)w * h);
        }
    }
}
