using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using iText.IO.Font.Constants;
using iText.Kernel.Font;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Layer;
using iText.Kernel.Pdf.Xobject;
using iText.IO.Image;
using Rectangle = iText.Kernel.Geom.Rectangle;
using PageSize = iText.Kernel.Geom.PageSize;

/// <summary>
/// File PDF tổng hợp mô phỏng bộ hồ sơ bản vẽ CAD xuất từ AutoCAD: khổ A1 ngang, 8 layer (OCG),
/// hàng chục nghìn đoạn thẳng/trang (nét vẽ, hatch), block khung tên + ký hiệu lặp lại (Form XObject),
/// vài trăm nhãn chữ. Dùng khi không có file thật — cấu trúc giống, không giống từng byte.
/// </summary>
static class Synthetic
{
    public static void Make(string path, int pages, int segmentsPerPage, bool withImage = true)
    {
        var sw = Stopwatch.StartNew();
        var rnd = new Random(12345);
        var inv = CultureInfo.InvariantCulture;
        using var doc = new PdfDocument(new PdfWriter(path, new WriterProperties().SetFullCompressionMode(true)));
        var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
        string[] layerNames = { "0", "TRUC", "NET_CHINH", "NET_MANH", "HATCH", "KICH_THUOC", "CHU", "KHUNG_TEN" };
        var layers = new PdfLayer[layerNames.Length];
        for (int i = 0; i < layers.Length; i++) layers[i] = new PdfLayer(layerNames[i], doc);

        // Block khung tên (3000 đoạn + chữ) và ký hiệu nhỏ (40 đoạn) — dùng lại mọi trang như block CAD.
        var titleBlock = new PdfFormXObject(new Rectangle(0, 0, 600, 200));
        titleBlock.GetPdfObject().SetData(Encoding.ASCII.GetBytes(Lines(rnd, 3000, 600, 200, inv)));
        var symbol = new PdfFormXObject(new Rectangle(0, 0, 20, 20));
        symbol.GetPdfObject().SetData(Encoding.ASCII.GetBytes(Lines(rnd, 40, 20, 20, inv)));

        // Ảnh raster dùng chung (kiểu logo/chữ ký scan độ phân giải cao trong khung tên): 2400x1600 xám.
        PdfImageXObject? image = null;
        if (withImage)
        {
            var px = new byte[2400 * 1600];
            for (int y = 0; y < 1600; y++)
                for (int x = 0; x < 2400; x++)
                    px[y * 2400 + x] = (byte)(((x / 7) ^ (y / 5)) * 3 + rnd.Next(24));
            image = new PdfImageXObject(ImageDataFactory.Create(2400, 1600, 1, 8, px, null));
        }

        const float W = 2384, H = 1684; // A1 ngang (pt)
        for (int p = 0; p < pages; p++)
        {
            var page = doc.AddNewPage(new PageSize(W, H));
            var res = page.GetResources();
            var fontName = res.AddFont(doc, font);
            var tbName = res.AddForm(titleBlock);
            var symName = res.AddForm(symbol);
            var imgName = image != null ? res.AddImage(image) : null;
            var layerRes = new PdfName[layers.Length];
            for (int i = 0; i < layers.Length; i++) layerRes[i] = res.AddProperties(layers[i].GetPdfObject());

            var sb = new StringBuilder(segmentsPerPage * 40);
            int perLayer = segmentsPerPage / 6;
            for (int l = 1; l <= 5; l++)
            {
                sb.Append('/').Append("OC /").Append(layerRes[l].GetValue()).Append(" BDC\n");
                sb.Append(inv, $"{0.2 + l * 0.15:F2} w {l * 0.1:F1} 0 {1 - l * 0.15:F2} RG\n");
                // Cụm nét dày đặc quanh vài "khu vực bản vẽ" như mặt bằng/trắc dọc thật.
                for (int k = 0; k < perLayer; k++)
                {
                    double cx = 150 + rnd.NextDouble() * (W - 900), cy = 150 + rnd.NextDouble() * (H - 300);
                    double x2 = cx + (rnd.NextDouble() - 0.5) * 30, y2 = cy + (rnd.NextDouble() - 0.5) * 30;
                    sb.Append(inv, $"{cx:F2} {cy:F2} m {x2:F2} {y2:F2} l\n");
                    if (k % 64 == 63) sb.Append("S\n");
                }
                sb.Append("S\nEMC\n");
            }
            // Hatch: đoạn song song dày
            sb.Append("/OC /").Append(layerRes[4].GetValue()).Append(" BDC 0.1 w 0.5 0.5 0.5 RG\n");
            for (int k = 0; k < segmentsPerPage - perLayer * 5; k++)
            {
                double x = 400 + (k % 400) * 2.5, y = 300 + (k / 400) * 2.5;
                sb.Append(inv, $"{x:F2} {y:F2} m {x + 2:F2} {y + 2:F2} l\n");
                if (k % 64 == 63) sb.Append("S\n");
            }
            sb.Append("S\nEMC\n");
            // Ký hiệu lặp lại (block) + chữ
            sb.Append("/OC /").Append(layerRes[6].GetValue()).Append(" BDC\n");
            for (int k = 0; k < 300; k++)
                sb.Append(inv, $"q 1 0 0 1 {100 + rnd.NextDouble() * (W - 200):F2} {100 + rnd.NextDouble() * (H - 200):F2} cm /{symName.GetValue()} Do Q\n");
            sb.Append("0 g\n");
            for (int k = 0; k < 400; k++)
                sb.Append(inv, $"BT /{fontName.GetValue()} 5 Tf {100 + rnd.NextDouble() * (W - 300):F2} {100 + rnd.NextDouble() * (H - 200):F2} Td (KM{p}+{k:000} CAO DO {rnd.Next(100, 999)}) Tj ET\n");
            sb.Append("EMC\n");
            sb.Append("/OC /").Append(layerRes[7].GetValue()).Append(" BDC q 1 0 0 1 ").Append((W - 640).ToString(inv))
              .Append(" 20 cm /").Append(tbName.GetValue()).Append(" Do Q EMC\n");

            if (imgName != null)
                sb.Append("q 480 0 0 320 ").Append((W - 640).ToString(inv)).Append(" 240 cm /").Append(imgName.GetValue()).Append(" Do Q\n");
            var content = new PdfStream(Encoding.ASCII.GetBytes(sb.ToString()));
            content.SetCompressionLevel(CompressionConstants.DEFAULT_COMPRESSION);
            page.GetPdfObject().Put(PdfName.Contents, content.MakeIndirect(doc));
            page.Flush();
            if ((p + 1) % 20 == 0) Console.WriteLine($"  {p + 1}/{pages} trang…");
        }
        doc.Close();
        Console.WriteLine($"Đã tạo {path}: {pages} trang, {new FileInfo(path).Length / 1048576.0:F1} MB, {sw.Elapsed.TotalSeconds:F0} s");
    }

    static string Lines(Random rnd, int count, double w, double h, IFormatProvider inv)
    {
        var sb = new StringBuilder("0.3 w 0 G\n");
        for (int i = 0; i < count; i++)
        {
            sb.Append(string.Format(inv, "{0:F2} {1:F2} m {2:F2} {3:F2} l\n", rnd.NextDouble() * w, rnd.NextDouble() * h, rnd.NextDouble() * w, rnd.NextDouble() * h));
            if (i % 64 == 63) sb.Append("S\n");
        }
        return sb.Append("S\n").ToString();
    }
}
