using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace XTPdfMergeApp.Services
{
    public enum PrintScale { FitToPaper, ActualSize, Custom, ReduceToPaper }
    public enum PrintOrientation { Auto, Portrait, Landscape }
    public enum PrintColor { Color, Grayscale, BlackLines }
    public enum PrintQuality { Standard, CadHigh }

    /// <summary>Yêu cầu in: các trang (theo thứ tự in), máy in, khổ giấy, số bản, tỉ lệ, màu.</summary>
    public sealed record PrintRequest(
        IReadOnlyList<(string SourcePath, int PageNumber)> Pages, string Printer, PaperSize Paper, int Copies,
        PrintScale Scale, int CustomPercent, PrintColor Color, PrintQuality Quality, PrintOrientation Orientation, bool Center, bool Collate, byte[]? DevMode);

    /// <summary>The printer driver's own settings dialog (paper tray, quality, duplex, plotter options…) through Win32 DocumentProperties. The result is a DEVMODE blob that is applied to the print job.</summary>
    public static class PrinterDriver
    {
        private const int DM_OUT_BUFFER = 2, DM_IN_BUFFER = 8, DM_IN_PROMPT = 4, IDOK = 1;
        private const uint GMEM_MOVEABLE = 0x2;

        [System.Runtime.InteropServices.DllImport("winspool.drv", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern bool OpenPrinter(string name, out IntPtr handle, IntPtr defaults);
        [System.Runtime.InteropServices.DllImport("winspool.drv", SetLastError = true)]
        private static extern bool ClosePrinter(IntPtr handle);
        [System.Runtime.InteropServices.DllImport("winspool.drv", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern int DocumentProperties(IntPtr hwnd, IntPtr printer, string device, IntPtr output, IntPtr input, int mode);
        [System.Runtime.InteropServices.DllImport("winspool.drv", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern int DeviceCapabilities(string device, string? port, short capability, IntPtr output, IntPtr devMode);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr handle);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr handle);

        /// <summary>Shows the driver dialog; returns the new DEVMODE, or null when cancelled / not available.</summary>
        public static byte[]? ShowDialog(IntPtr owner, string printerName, byte[]? current)
        {
            if (!OpenPrinter(printerName, out var printer, IntPtr.Zero)) return null;
            IntPtr input = IntPtr.Zero, output = IntPtr.Zero;
            try
            {
                int size = DocumentProperties(owner, printer, printerName, IntPtr.Zero, IntPtr.Zero, 0);
                if (size <= 0) return null;
                output = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
                int mode = DM_IN_PROMPT | DM_OUT_BUFFER;
                if (current != null && current.Length >= size)
                {
                    input = System.Runtime.InteropServices.Marshal.AllocHGlobal(current.Length);
                    System.Runtime.InteropServices.Marshal.Copy(current, 0, input, current.Length);
                    mode |= DM_IN_BUFFER;
                }
                if (DocumentProperties(owner, printer, printerName, output, input, mode) != IDOK) return null;
                var result = new byte[size];
                System.Runtime.InteropServices.Marshal.Copy(output, result, 0, size);
                return result;
            }
            finally
            {
                if (input != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeHGlobal(input);
                if (output != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeHGlobal(output);
                ClosePrinter(printer);
            }
        }

        /// <summary>Applies a DEVMODE from <see cref="ShowDialog"/> to the printer settings of a document.</summary>
        public static void Apply(PrintDocument document, byte[] devMode)
        {
            IntPtr handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)devMode.Length);
            if (handle == IntPtr.Zero) return;
            try
            {
                IntPtr locked = GlobalLock(handle);
                System.Runtime.InteropServices.Marshal.Copy(devMode, 0, locked, devMode.Length);
                GlobalUnlock(handle);
                document.PrinterSettings.SetHdevmode(handle);
                document.DefaultPageSettings.SetHdevmode(handle);
            }
            finally { GlobalFree(handle); }
        }

        /// <summary>Paper size, copies and collate chosen in the driver dialog (so our own controls can follow it).</summary>
        public static (PaperSize? Paper, short Copies, bool Collate) Read(string printerName, byte[] devMode)
        {
            var document = new PrintDocument();
            document.PrinterSettings.PrinterName = printerName;
            Apply(document, devMode);
            return (document.DefaultPageSettings.PaperSize, document.PrinterSettings.Copies, document.PrinterSettings.Collate);
        }

        private const short DC_COLLATE = 22;

        /// <summary>The driver can collate copies itself (otherwise the box is off and unchecked, as in the driver's own dialog).</summary>
        public static bool SupportsCollate(string printerName)
        {
            try { return DeviceCapabilities(printerName, null, DC_COLLATE, IntPtr.Zero, IntPtr.Zero) == 1; }
            catch { return true; }
        }

        /// <summary>The printer's own default DEVMODE (no dialog).</summary>
        public static byte[]? GetDefault(string printerName)
        {
            if (!OpenPrinter(printerName, out var printer, IntPtr.Zero)) return null;
            IntPtr output = IntPtr.Zero;
            try
            {
                int size = DocumentProperties(IntPtr.Zero, printer, printerName, IntPtr.Zero, IntPtr.Zero, 0);
                if (size <= 0) return null;
                output = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
                if (DocumentProperties(IntPtr.Zero, printer, printerName, output, IntPtr.Zero, DM_OUT_BUFFER) != IDOK) return null;
                var result = new byte[size];
                System.Runtime.InteropServices.Marshal.Copy(output, result, 0, size);
                return result;
            }
            finally
            {
                if (output != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeHGlobal(output);
                ClosePrinter(printer);
            }
        }

        /// <summary>
        /// The DEVMODE with our own settings put into it: collate, copies and paper. The driver's DEVMODE is the one truth: the print dialog writes its boxes into it before the driver's
        /// Properties dialog opens and before printing, and reads it back after Properties, so the two places never show different things (the fault of some print dialogs).
        /// </summary>
        public static byte[]? WithSettings(string printerName, byte[]? devMode, bool? collate, int? copies, PaperSize? paper)
        {
            var document = new PrintDocument();
            document.PrinterSettings.PrinterName = printerName;
            if (!document.PrinterSettings.IsValid) return devMode;
            var baseMode = devMode ?? GetDefault(printerName);
            if (baseMode != null) Apply(document, baseMode);
            if (collate is { } c) document.PrinterSettings.Collate = c;
            if (copies is { } n) document.PrinterSettings.Copies = (short)Math.Clamp(n, 1, 9999);
            if (paper != null) document.DefaultPageSettings.PaperSize = paper;
            IntPtr handle = document.PrinterSettings.GetHdevmode(document.DefaultPageSettings);
            if (handle == IntPtr.Zero) return devMode;
            try
            {
                int size = (int)GlobalSize(handle);
                IntPtr locked = GlobalLock(handle);
                var result = new byte[size];
                System.Runtime.InteropServices.Marshal.Copy(locked, result, 0, size);
                GlobalUnlock(handle);
                return result;
            }
            finally { GlobalFree(handle); }
        }
    }

    /// <summary>In PDF theo trạng thái layer đang xem. Chất lượng CAD 600 DPI được render theo dải để không phải giữ cả A3/A0 trong RAM.</summary>
    public static class PdfPrintService
    {
        private const int StandardDpi = 300;
        private const int CadHighDpi = 600;
        private const int MaxRasterDimension = 20_000;
        private const int MaxBandPixels = 12_000_000;

        /// <summary>Kế hoạch raster theo hundredths of an inch. Band giới hạn peak memory của ảnh WPF + buffer BGRA + GDI bitmap.</summary>
        internal readonly record struct RasterPlan(int FullWidth, int FullHeight, int BandHeight, int EffectiveDpi);

        internal static RasterPlan GetRasterPlan(double drawWidth, double drawHeight, PrintQuality quality)
        {
            int requestedDpi = quality == PrintQuality.CadHigh ? CadHighDpi : StandardDpi;
            double rawWidth = Math.Max(1, drawWidth / 100.0 * requestedDpi);
            double rawHeight = Math.Max(1, drawHeight / 100.0 * requestedDpi);
            double reduction = Math.Min(1.0, MaxRasterDimension / Math.Max(rawWidth, rawHeight));
            int fullWidth = Math.Max(1, (int)Math.Round(rawWidth * reduction));
            int fullHeight = Math.Max(1, (int)Math.Round(rawHeight * reduction));
            int bandHeight = Math.Clamp(MaxBandPixels / fullWidth, 1, fullHeight);
            int effectiveDpi = Math.Max(1, (int)Math.Round(requestedDpi * reduction));
            return new RasterPlan(fullWidth, fullHeight, bandHeight, effectiveDpi);
        }

        /// <summary>Khổ trang (point, đã tính xoay) của 1 trang — để chọn hướng giấy và tỉ lệ.</summary>
        private static (double W, double H) PageSize(string path, int pageNumber)
        {
            var sizes = PdfThumbnailService.GetPageSizesAsync(path).GetAwaiter().GetResult();
            return sizes != null && pageNumber >= 1 && pageNumber <= sizes.Length && sizes[pageNumber - 1].Width > 0 ? sizes[pageNumber - 1] : (595, 842);
        }

        public static Task<bool> PrintAsync(PrintRequest request, IProgress<int>? progress = null) => Task.Run(() =>
        {
            foreach (string path in request.Pages.Select(p => p.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase))
                PdfPermissionPolicy.EnsureAllowed(path, PdfPermissionOperation.Print);
            var document = new PrintDocument();
            document.PrinterSettings.PrinterName = request.Printer;
            if (!document.PrinterSettings.IsValid) return false;
            if (request.DevMode != null) PrinterDriver.Apply(document, request.DevMode);
            document.PrinterSettings.Copies = (short)Math.Clamp(request.Copies, 1, 99);
            if (PrinterDriver.SupportsCollate(request.Printer)) document.PrinterSettings.Collate = request.Collate;
            document.DefaultPageSettings.PaperSize = request.Paper;
            document.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
            document.DocumentName = "PDF Reader Pro";
            document.PrintController = new StandardPrintController();

            int index = 0;
            var sizeCache = new Dictionary<string, (double W, double H)>();
            (double W, double H) SizeOf(int i)
            {
                var (path, number) = request.Pages[i];
                string key = path + "|" + number;
                if (!sizeCache.TryGetValue(key, out var size)) sizeCache[key] = size = PageSize(path, number);
                return size;
            }

            document.QueryPageSettings += (_, e) =>
            {
                if (index >= request.Pages.Count) return;
                var (w, h) = SizeOf(index);
                e.PageSettings.Landscape = request.Orientation switch { PrintOrientation.Landscape => true, PrintOrientation.Portrait => false, _ => w > h };
            };
            document.PrintPage += (_, e) =>
            {
                var (path, number) = request.Pages[index];
                var (wPt, hPt) = SizeOf(index);
                RectangleF area = e.MarginBounds.Width > 0 ? e.MarginBounds : e.PageBounds;
                double pageW = wPt / 72.0 * 100, pageH = hPt / 72.0 * 100; // trăm inch
                double scale = request.Scale switch
                {
                    PrintScale.ActualSize => 1.0,
                    PrintScale.Custom => Math.Clamp(request.CustomPercent, 5, 1000) / 100.0,
                    PrintScale.ReduceToPaper => Math.Min(1.0, Math.Min(area.Width / pageW, area.Height / pageH)),
                    _ => Math.Min(area.Width / pageW, area.Height / pageH)
                };
                double drawW = pageW * scale, drawH = pageH * scale;
                var plan = GetRasterPlan(drawW, drawH, request.Quality);
                var g = e.Graphics!;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float x = request.Center ? area.Left + (float)((area.Width - drawW) / 2) : area.Left;
                float y = request.Center ? area.Top + (float)((area.Height - drawH) / 2) : area.Top;
                for (int top = 0; top < plan.FullHeight; top += plan.BandHeight)
                {
                    int height = Math.Min(plan.BandHeight, plan.FullHeight - top);
                    var tile = new System.Windows.Int32Rect(0, top, plan.FullWidth, height);
                    var source = PdfThumbnailService.RenderPageTilesBatchAsync(path, number - 1, plan.FullWidth, plan.FullHeight,
                        new[] { tile }, layerToken: PdfLayerStateStore.GetToken(path), withAnnotations: true).GetAwaiter().GetResult()[0];
                    if (source == null) throw new InvalidOperationException($"Could not render page {number} for printing.");
                    using var bitmap = ToBitmap(source, request.Color);
                    float bandY = y + (float)(drawH * top / plan.FullHeight);
                    float bandH = (float)(drawH * height / plan.FullHeight);
                    g.DrawImage(bitmap, x, bandY, (float)drawW, bandH);
                }
                index++;
                progress?.Report(index);
                e.HasMorePages = index < request.Pages.Count;
            };
            document.Print();
            return true;
        });

        /// <summary>Ảnh WPF (BGRA) → System.Drawing.Bitmap, đổi màu theo chế độ in.</summary>
        internal static Bitmap ToBitmap(BitmapSource source, PrintColor color)
        {
            var converted = source.Format == System.Windows.Media.PixelFormats.Bgra32 || source.Format == System.Windows.Media.PixelFormats.Pbgra32 || source.Format == System.Windows.Media.PixelFormats.Bgr32
                ? source : new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            int w = converted.PixelWidth, h = converted.PixelHeight, stride = w * 4;
            var pixels = new byte[stride * h];
            converted.CopyPixels(pixels, stride, 0);
            if (color != PrintColor.Color)
            {
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    int gray = (pixels[i + 2] * 77 + pixels[i + 1] * 150 + pixels[i] * 29) >> 8;
                    if (color == PrintColor.BlackLines) gray = gray < 215 ? 0 : 255;
                    pixels[i] = pixels[i + 1] = pixels[i + 2] = (byte)gray;
                    pixels[i + 3] = 255;
                }
            }
            var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var data = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < h; y++)
                    System.Runtime.InteropServices.Marshal.Copy(pixels, y * stride, data.Scan0 + y * data.Stride, stride);
            }
            finally { bitmap.UnlockBits(data); }
            return bitmap;
        }

        /// <summary>"1-12, 40, 55-60" → danh sách số trang 1-based (theo thứ tự nhập, bỏ trang ngoài [1, max]); null nếu sai cú pháp.</summary>
        public static List<int>? ParseRange(string text, int max)
        {
            var result = new List<int>();
            foreach (string part in text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var bounds = part.Split('-', StringSplitOptions.TrimEntries);
                if (bounds.Length == 1 && int.TryParse(bounds[0], out int single)) { if (single >= 1 && single <= max) result.Add(single); }
                else if (bounds.Length == 2 && int.TryParse(bounds[0], out int a) && int.TryParse(bounds[1], out int b))
                {
                    if (a > b) (a, b) = (b, a);
                    for (int p = Math.Max(1, a); p <= Math.Min(max, b); p++) result.Add(p);
                }
                else return null;
            }
            return result;
        }
    }
}
