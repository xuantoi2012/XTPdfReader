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

Các tuỳ chọn thêm:
- `--mem`: đọc tuần tự CẢ file vào RAM trước, rồi `FPDF_LoadMemDocument64`, giống app sau bước #0.
  Đo trước/sau #0 bằng cách chạy lần lượt `bench file.pdf` và `bench file.pdf --mem`.
- `--netsim <độ_trễ_ms> <MB/s>`: giả lập ổ mạng. Mỗi lần PDFium nhảy vị trí đọc tốn 1 vòng độ trễ,
  cộng thời gian truyền. Ví dụ: `bench file.pdf --netsim 5 60`.
- Mục **"0. Kiểu đọc I/O"** đếm số lần đọc, số lần nhảy vị trí và số KB PDFium đọc khi mở file và
  parse 20 trang đầu. Qua SMB, mỗi lần nhảy vị trí là 1 vòng hỏi-đáp.

## Đo khoảng thời gian đọc nền (#0)

```powershell
# Tốc độ đọc tuần tự của ổ: khối 1/4/16 MB × 1/2/4 luồng song song (Windows: bỏ qua cache hệ điều hành)
dotnet run -c Release -- readtest "P:\...\file.pdf"
# Độ trễ mỗi trang mới trong lúc đang đọc nền:
#   A = kiểu cũ (không đọc nền), B = #0 cũ (đọc hết file ở nền, tranh băng thông),
#   C = mô hình phương án C: cache khối --block KB, trang ưu tiên hơn đọc nền (--bgkb KB mỗi lần)
#   D = CODE THẬT của app (Services/PdfFileBuffer.cs + LayeredDocumentSource.cs, link vào PdfBench):
#       khối 256 KB, đọc nền 256 KB/lượt — không phụ thuộc --block/--bgkb
dotnet run -c Release -- window "P:\...\file.pdf" --block 256 --bgkb 256
dotnet run -c Release -- window file.pdf --netsim 6 21 --block 256 --bgkb 256 --pages 20
```

## Đo cuộn liên tục: 1 tiến trình so với N tiến trình

```powershell
dotnet run -c Release -- scroll "P:\...\file.pdf"                  # 3 trang/màn, 5 màn
dotnet run -c Release -- scroll "P:\...\file.pdf" --visible 2      # 2 trang/màn
# --width PX: độ rộng ảnh nét (mặc định: N trang vừa khung nhìn cao --viewport 880 px, lượng tử 256 px)
# --ram-only / --net-only; --netsim ms MBps (giả lập, chỉ để thử khi không có ổ mạng)
```

Mỗi màn có N trang mới. Mỗi trang được parse, vẽ ảnh xem trước 340 px, rồi vẽ ảnh nét. Công cụ so 2
cách:
- **1 tiến trình:** như app hiện tại.
- **N tiến trình worker thật:** mỗi worker có PDFium riêng và được khởi động sẵn. Worker nhận lệnh qua
  pipe và trả bitmap qua memory-mapped file.

Thời gian mỗi trang được tách thành chờ đọc file, parse CPU và vẽ CPU. Có 2 chế độ:
- **RAM:** như sau khi đọc nền xong, chỉ còn CPU.
- **net:** bộ đệm chưa có gì, mỗi worker tự đọc file riêng.

## Tạo file tổng hợp khi không có file thật

```powershell
dotnet run -c Release -- make synthetic.pdf 220 60000        # ~159 MB, 220 trang A1, có ảnh raster
dotnet run -c Release -- make synthetic.pdf 220 60000 noimage
# Mỗi trang 16 block ghi xen kẽ khắp file → ~50 lần đọc nhỏ/trang, giống kiểu đọc đo trên file thật:
dotnet run -c Release -- make scatter.pdf 280 60000 scatter
```

File tổng hợp gồm 8 layer (OCG) và khoảng 60.000 đoạn thẳng mỗi trang. Khung tên và ký hiệu là block
(Form XObject) dùng lại, có 400 nhãn chữ và 1 ảnh xám 2400x1600. File này mô phỏng cấu trúc bản vẽ
xuất từ AutoCAD, nhưng không thay được file thật.
