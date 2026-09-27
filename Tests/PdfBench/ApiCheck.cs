using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using XTPdfMergeApp.Services;

/// <summary>
/// apicheck — bước 2: app gọi PDFium qua PdfiumInstance (con trỏ hàm lấy bằng NativeLibrary) thay cho
/// [DllImport("pdfium")]. Vẽ cùng các trang theo 2 đường, đúng cách app vẽ (progressive, cờ của app), so ảnh
/// từng bit, cỡ trang, và thời gian. Thêm: mở file có tên tiếng Việt qua FPDF_LoadDocument (chuỗi UTF-8).
/// </summary>
static unsafe class ApiCheck
{
    const int Flags = 0x01 | 0x02 | 0x04 | 0x200; // FPDF_ANNOT | LCD_TEXT | NO_NATIVETEXT | RENDER_LIMITEDIMAGECACHE
    [DllImport("pdfium")] static extern IntPtr FPDFBitmap_GetBuffer(IntPtr b);
    [DllImport("pdfium")] static extern int FPDFBitmap_GetStride(IntPtr b);
    [DllImport("pdfium")] static extern int FPDF_GetPageSizeByIndex(IntPtr d, int i, out double w, out double h);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PauseCb(IntPtr self);
    [StructLayout(LayoutKind.Sequential)] struct Pause { public int Version; public IntPtr NeedToPauseNow; public IntPtr User; }

    public static int Run(string path, string[] opts)
    {
        int pages = opts.Length > 0 && int.TryParse(opts[0], out int n) ? n : 30;
        int width = 1024, fails = 0;
        Native.FPDF_InitLibrary();
        var api = PdfiumInstance.Primary;
        api.EnsureInitialized();
        var bytes = File.ReadAllBytes(path);

        // Cả 2 đường mở cùng file từ đĩa (FPDF_LoadDocument), như app khi file quá lớn để đệm.
        IntPtr docOld = Native.FPDF_LoadDocument(path, null);
        IntPtr docNew = api.LoadDocument(path);
        int count = Native.FPDF_GetPageCount(docOld);
        Console.WriteLine($"File {bytes.Length / 1048576.0:F0} MB, {count} trang. So {Math.Min(pages, count)} trang, rộng {width} px, vẽ progressive như app.");
        if (docNew == IntPtr.Zero || api.GetPageCount(docNew) != count) { Console.WriteLine("FAIL: PdfiumInstance không mở được / số trang khác"); return 1; }

        var cb = new PauseCb(_ => 0);
        IntPtr pause = Marshal.AllocHGlobal(Marshal.SizeOf<Pause>());
        Marshal.StructureToPtr(new Pause { Version = 1, NeedToPauseNow = Marshal.GetFunctionPointerForDelegate(cb) }, pause, false);

        var tOld = new List<double>(); var tNew = new List<double>();
        for (int i = 0; i < Math.Min(pages, count); i++)
        {
            // Xen kẽ thứ tự để không bên nào luôn được hưởng cache CPU/đĩa của bên kia.
            ulong hOld, hNew;
            if (i % 2 == 0) { hOld = RenderOld(docOld, i, width, pause, tOld); hNew = RenderNew(api, docNew, i, width, pause, tNew); }
            else { hNew = RenderNew(api, docNew, i, width, pause, tNew); hOld = RenderOld(docOld, i, width, pause, tOld); }
            FPDF_GetPageSizeByIndex(docOld, i, out double w1, out double h1);
            bool okSize = api.GetPageSizeByIndex(docNew, i, out double w2, out double h2) && w1 == w2 && h1 == h2;
            if (hOld != hNew || !okSize) { fails++; Console.WriteLine($"FAIL trang {i + 1}: ảnh {(hOld == hNew ? "giống" : "KHÁC")}, cỡ trang {(okSize ? "giống" : "KHÁC")}"); }
        }
        GC.KeepAlive(cb); Marshal.FreeHGlobal(pause);
        Native.FPDF_CloseDocument(docOld); api.CloseDocument(docNew);
        Console.WriteLine($"Ảnh + cỡ trang giống hệt: {Math.Min(pages, count) - fails}/{Math.Min(pages, count)} trang");
        Stat("[DllImport] (cũ)", tOld);
        Stat("PdfiumInstance (mới)", tNew);
        Console.WriteLine($"   → mới/cũ: {tNew.Average() / tOld.Average():P0} thời gian");

        // Tên file tiếng Việt: FPDF_LoadDocument nhận UTF-8.
        string dir = Path.Combine(Path.GetTempPath(), "pdfbench-apicheck");
        Directory.CreateDirectory(dir);
        string vn = Path.Combine(dir, "Bản vẽ ĐƯỜNG ống – tầng 2 (đã sửa).pdf");
        File.Copy(path, vn, true);
        IntPtr d = api.LoadDocument(vn);
        bool vnOk = d != IntPtr.Zero && api.GetPageCount(d) == count;
        if (d != IntPtr.Zero) api.CloseDocument(d);
        try { File.Delete(vn); } catch { }
        Console.WriteLine((vnOk ? "PASS" : "FAIL") + " mở file tên tiếng Việt qua PdfiumInstance.LoadDocument");
        if (!vnOk) fails++;
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        return fails == 0 ? 0 : 1;
    }

    static ulong RenderOld(IntPtr doc, int index, int width, IntPtr pause, List<double> times)
    {
        var sw = Stopwatch.StartNew();
        IntPtr page = Native.FPDF_LoadPage(doc, index);
        int height = (int)Math.Round(width * Native.FPDF_GetPageHeight(page) / Native.FPDF_GetPageWidth(page));
        IntPtr bmp = Native.FPDFBitmap_Create(width, height, 1);
        Native.FPDFBitmap_FillRect(bmp, 0, 0, width, height, 0xFFFFFFFF);
        int status = Native.FPDF_RenderPageBitmap_Start(bmp, page, 0, 0, width, height, 0, Flags, pause);
        while (status == 1) status = Native.FPDF_RenderPage_Continue(page, pause);
        Native.FPDF_RenderPage_Close(page);
        times.Add(sw.Elapsed.TotalMilliseconds);
        ulong h = Hash((byte*)FPDFBitmap_GetBuffer(bmp), FPDFBitmap_GetStride(bmp) * height);
        Native.FPDFBitmap_Destroy(bmp);
        Native.FPDF_ClosePage(page);
        return h;
    }

    static ulong RenderNew(PdfiumInstance api, IntPtr doc, int index, int width, IntPtr pause, List<double> times)
    {
        var sw = Stopwatch.StartNew();
        IntPtr page = api.LoadPage(doc, index);
        int height = (int)Math.Round(width * api.GetPageHeight(page) / api.GetPageWidth(page));
        IntPtr bmp = api.BitmapCreate(width, height, 1);
        api.BitmapFillRect(bmp, 0, 0, width, height, 0xFFFFFFFF);
        int status = api.RenderPageBitmapStart(bmp, page, 0, 0, width, height, 0, Flags, pause);
        while (status == 1) status = api.RenderPageContinue(page, pause);
        api.RenderPageClose(page);
        times.Add(sw.Elapsed.TotalMilliseconds);
        ulong h = Hash((byte*)api.BitmapGetBuffer(bmp), api.BitmapGetStride(bmp) * height);
        api.BitmapDestroy(bmp);
        api.ClosePage(page);
        return h;
    }

    static ulong Hash(byte* p, long n)
    {
        ulong x = 1469598103934665603;
        for (long i = 0; i + 8 <= n; i += 8) x = (x ^ *(ulong*)(p + i)) * 1099511628211;
        return x;
    }

    static void Stat(string label, List<double> v)
    {
        var s = v.OrderBy(x => x).ToList();
        Console.WriteLine($"   {label,-24} TB {v.Average(),6:F1}  trung vị {s[s.Count / 2],6:F1}  max {s[^1],6:F1} ms/trang (n={v.Count})");
    }
}
