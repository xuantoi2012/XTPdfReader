# PdfBench — đo hiệu năng PDFium đúng như XTPdfMergeApp dùng

Công cụ console không dùng WPF (chạy được trên Windows và Linux). Nó gọi đúng các hàm PDFium mà
`PdfThumbnailService` gọi, với các thông số giống app:
- Cờ vẽ `FPDF_ANNOT | LCD_TEXT | NO_NATIVETEXT | RENDER_LIMITEDIMAGECACHE`.
- Vẽ progressive `_Start/_Continue`, mỗi lát 8 ms.
- Tile 640 px, ảnh Viewer lượng tử 256 px (512–2304), thumbnail 340 px.

Kết quả là thời gian của riêng PDFium. Phần WPF (chép bitmap, layout, hiển thị) xem trong bảng
"Debug tải PDF" của app.

## Đo trên file thật (Windows)

```powershell
cd Tests\PdfBench
dotnet run -c Release -- bench "P:\...\03. QUYEN 2.2 - TNM, TNT, HKT, CAY XANH, CHIEU SANG, TCTC.pdf"
# Màn hình/DPI khác mặc định (1920x1080, 100%):
dotnet run -c Release -- bench "file.pdf" --screen 2560x1440 --dpi 1.25
```

Nên chạy 2 lần. Lần 1 có thể chậm hơn do Windows chưa cache file ở ổ mạng/đĩa. Gửi lại toàn bộ
kết quả in ra.

## Tạo file tổng hợp khi không có file thật

```powershell
dotnet run -c Release -- make synthetic.pdf 220 60000        # ~159 MB, 220 trang A1, có ảnh raster
dotnet run -c Release -- make synthetic.pdf 220 60000 noimage
```

File tổng hợp gồm 8 layer (OCG) và khoảng 60.000 đoạn thẳng mỗi trang. Khung tên và ký hiệu là block
(Form XObject) dùng lại, có 400 nhãn chữ và 1 ảnh xám 2400x1600. File này mô phỏng cấu trúc bản vẽ
xuất từ AutoCAD, nhưng không thay được file thật.
