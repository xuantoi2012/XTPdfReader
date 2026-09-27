# Báo cáo hiệu năng mở / zoom / cuộn — trước khi sửa (2026-09-27)

## 0. Đo trên file nào

File thật `P:\...\03. QUYEN 2.2 - TNM, TNT, HKT, CAY XANH, CHIEU SANG, TCTC.pdf` (~163 MB) **không
truy cập được**. Sandbox là container Linux trên cloud, không có ổ P: và không có đường vào mạng nội bộ.
File này cũng không đưa qua git được, vì GitHub chặn file trên 100 MB nếu không có LFS.

Vì vậy tôi đã làm 2 việc:
1. **Đo trên file tổng hợp tương đương**: 159 MB, 220 trang A1, 8 layer, khoảng 60.000 nét/trang,
   block khung tên và ký hiệu (Form XObject), 400 nhãn chữ, 1 ảnh raster. Bộ tạo file nằm ở
   `Tests/PdfBench`.
2. **Viết công cụ đo `Tests/PdfBench`** để anh chạy trên Windows với file thật (xem `README.md` cùng
   thư mục). Chỉ cần gửi lại kết quả in ra, không cần gửi file.

Đo bằng **đúng bản PDFium 147.0.7690 của app**, bản Linux x64, 4 nhân. Máy Windows của anh sẽ ra số
tuyệt đối khác, nhưng tỉ lệ giữa các bước vẫn dùng được.

Phần WPF (chép bitmap, layout, vẽ lên màn hình) **không chạy được trên Linux** nên chưa đo. Trên máy
thật, bảng "Debug tải PDF" của app có sẵn số liệu từng bước: mở document, parse trang, chờ gate,
lát raster, chép bitmap WPF, hàng đợi UI.

## 1. Số đo (file tổng hợp 159 MB, màn hình 1920x1080, ảnh Viewer fit-width 1792 px)

| Việc | Thời gian PDFium |
|---|---:|
| `FPDF_LoadDocument` (mở file 159 MB) | **1–2 ms** |
| Parse trang 1 (`FPDF_LoadPage`) | 39–68 ms |
| Vẽ trang 1 fit-width 1792 px (progressive, 14 lát) | 214–273 ms |
| **Mở file → trang đầu vẽ xong** | **255–343 ms** |
| 4 thumbnail đầu (app làm ngay khi mở, tuần tự) | 411–436 ms |
| Zoom 50% / 100% / 200% / 400%: tile đầu tiên | 59 / 41 / 23 / 16 ms |
| Zoom 50% / 100% / 200% / 400%: đủ vùng xem (4–9 tile 640 px) | 161 / 175 / 191 / 123 ms |
| Cuộn: mỗi trang MỚI (parse + vẽ 1792 px) | TB **259 ms** (max 287) |
| Cuộn: vẽ lại trang đã parse | 186 ms |
| Thumbnail 340 px | 65 ms |

**Vẽ theo độ rộng** (trang đã parse): 256 px = 57 ms · 512 px = 79 ms · 1024 px = 120 ms ·
1792 px = 186 ms · 2304 px = 252 ms. Chi phí chủ yếu nằm ở **số đối tượng** cần duyệt, không phải
số pixel: ảnh 512 px ít hơn 12 lần số pixel nhưng chỉ nhanh hơn khoảng 2,4 lần.

**Tile so với cả vùng xem**, zoom 200%, cùng một vùng 1920x1920:
- Chia 9 tile 640 px: 195 ms. Mỗi tile phải duyệt lại toàn bộ đối tượng của trang.
- Vẽ 1 lần cả vùng: **134 ms**.
- Với bản vẽ CAD dày nét, chia tile tốn **thêm khoảng 45%** thời gian.

**Lát progressive**: app đặt mỗi lát 8 ms, nhưng đo thực tế **lát dài nhất 69–72 ms**, vì PDFium chỉ
dừng giữa các đối tượng. Parse trang thì không chia lát được. Cả hai đều giữ gate PDFium toàn cục,
nên việc khác phải chờ.

**Cờ `FPDF_RENDER_LIMITEDIMAGECACHE`**: bật (như app) hay bỏ đều cho khoảng 185–190 ms. Với ảnh Flate
thì **không đo thấy khác biệt**. Với ảnh JPEG hoặc scan lớn trong file thật thì cần đo lại.

### So sánh engine trên cùng file tổng hợp (vẽ 1 trang ở 1792 px, gồm cả mở file)

| Engine | Trang 1 | 20 trang đầu |
|---|---:|---:|
| **PDFium (app đang dùng)** | **~0,26–0,34 s** | ~5,2 s (259 ms/trang) |
| Poppler 24.02 (`pdftoppm`) | 0,56 s | 11,1 s |
| MuPDF 1.23 (`mutool draw`) | 1,35 s | 25,6 s |

→ **Bản thân PDFium không chậm**: nhanh gấp 2–5 lần hai engine mã nguồn mở còn lại trên loại nội
dung này. Nếu app chậm hơn Foxit trên file thật, nguyên nhân nhiều khả năng nằm ở **cách app điều phối
việc vẽ**, hoặc ở đặc điểm riêng của file thật mà file tổng hợp chưa có (xem mục 4).

## 2. Đối chiếu pipeline với Chromium (`pdf/pdfium/pdfium_engine.cc`)

| Kỹ thuật của Chromium | App hiện tại | Vị trí trong code |
|---|---|---|
| Vẽ ngoài UI thread | **Có**: `Task.Run`, gate PDFium riêng, `BitmapSource` được `Freeze()` | `PdfThumbnailService.Progressive.cs` |
| Progressive `_Start/_Continue/_Close` | **Có**, lát 8 ms (thực tế tới ~70 ms) | cùng file trên |
| Vẽ theo vùng nhìn thấy | **Có, nhưng chia tile 640 px** (Chromium vẽ 1 vùng cho mỗi trang) | `ReaderWindow.xaml.cs` `ReaderTileSizePx` |
| Cuộn liên tục ảo hoá | **Có**: `VirtualizingStackPanel` Recycling | `ReaderWindow.xaml` |
| Tiền tải trang kế tiếp | **Chỉ ở chế độ 1 trang** (±2 trang). Ở **cuộn liên tục chỉ vẽ trang đang nằm trong viewport**, không tải trước | `QueueReaderAdjacentPrefetch`; `UpdateReaderContinuousTilesAsync` (`IsElementInViewport`) |
| Giữ trang đã parse | Cache page handle **4 trang rảnh** (dùng chung cho Viewer, tile, thumbnail) | `NativePageCacheCapacity = 4` |
| Hiện ảnh tạm trước khi có ảnh nét | Chỉ khi trang **đã có thumbnail**. Mở file lần đầu và trang mới cuộn tới thì **chờ ảnh nét** (không có ảnh độ phân giải thấp trước) | `ShowPageAsync`, `ReaderDisplayBitmap` |

## 3. Nút thắt, xếp theo mức ảnh hưởng (có số đo)

1. **Không có ảnh độ phân giải thấp trước.** Trang đầu và mỗi trang mới cuộn tới phải chờ đủ
   parse + vẽ 1792 px (~259 ms, file thật có thể lâu hơn). Nếu vẽ trước 512 px thì chỉ mất
   ~31 + 79 = **~110 ms**. Người dùng thấy trang nhanh hơn khoảng 2,3 lần, rồi ảnh nét thay vào sau.
2. **Cuộn liên tục không tải trước trang kế tiếp.** Mỗi trang chỉ bắt đầu vẽ khi đã lọt vào viewport,
   nên cuộn nhanh sẽ thấy trang trắng hoặc placeholder khoảng 260 ms mỗi trang, lần lượt từng trang.
3. **Zoom sâu chia tile 640 px** tốn thêm ~45% tổng thời gian trên bản vẽ dày nét (195 ms so với
   134 ms). Ưu điểm là tile đầu hiện rất nhanh (16–23 ms).
4. **Gate PDFium bị giữ lâu hơn thiết kế**: lát progressive tới ~70 ms và parse không chia được.
   Trong lúc đó thumbnail, tile và trang của Viewer phải xếp hàng chờ nhau. Thứ tự ưu tiên có sẵn chỉ
   có tác dụng giữa các lát.
5. **Cache page handle chỉ 4 trang rảnh.** Panel thumbnail (~6 trang đang hiện), Viewer (1–2 trang) và
   prefetch (±2 trang) cộng lại vượt 4. Trang bị đẩy ra rồi phải parse lại (31–68 ms/trang trên file
   tổng hợp; file CAD thật có thể nặng hơn nhiều).
6. *Chưa phải nút thắt trên file tổng hợp:* cờ `LIMITEDIMAGECACHE` và thời gian mở file (1–2 ms,
   PDFium đọc xref theo nhu cầu, 163 MB không làm chậm bước mở).

## 4. Chỉ biết được khi đo trên file thật

- Thời gian parse mỗi trang thật. File AutoCAD thường có XObject lồng nhau, chữ SHX xuất thành nét,
  và mẫu hatch. Nếu parse lên hàng trăm ms thì mục 3.4 và 3.5 thành nút thắt số 1.
- Ảnh raster (chữ ký scan, ảnh nền) và cờ `LIMITEDIMAGECACHE`.
- Phần WPF: xem bảng "Debug tải PDF" trong app.
- Foxit trên cùng máy: bấm giờ tay (mở file → trang đầu hiện, zoom 400% → nét, cuộn 20 trang).

## 5. Đề xuất sửa — giai đoạn 1 (mục tiêu bằng hoặc nhanh hơn Foxit), theo thứ tự ưu tiên

| # | Sửa | Lợi ích đo được | Rủi ro |
|---|---|---|---|
| 1 | **Ảnh thấp trước, ảnh nét sau**: trang đầu và mỗi trang mới vẽ trước ~512 px, hiện ngay, rồi thay bằng ảnh nét | Trang hiện sau ~110 ms thay vì ~260 ms (engine) | Thấp. Thêm 1 lần vẽ ~79 ms mỗi trang, chỉ khi trang chưa có ảnh nào |
| 2 | **Tải trước theo hướng cuộn** ở chế độ cuộn liên tục (1–2 trang kế tiếp, ưu tiên thấp hơn trang đang hiện) | Che được ~260 ms/trang khi cuộn đều | Thấp, chỉ tốn thêm RAM trong ngân sách cache sẵn có |
| 3 | **Cache page handle theo cửa sổ nhìn thấy + tải trước** (thay con số cố định 4) | Bỏ được lần parse lại, 31–68 ms/trang, nhiều hơn với file thật | Tốn RAM native. Cần giới hạn theo số trang |
| 4 | **Zoom sâu: vẽ vùng nhìn thấy của mỗi trang thành 1 lần progressive** như Chromium; tile chỉ dùng cho phần lộ ra khi pan | Nhanh hơn ~30% tổng (134 ms so với 195 ms) | Trung bình, vì đụng code tile/giữ ảnh cũ. Cần đo A/B trên file thật (README cũ đo trên trang nhẹ thì tile không thua) |
| 5 | **Tạm dừng thumbnail/prefetch khi người dùng đang zoom/pan**, giảm việc chặn gate | Giảm độ trễ tile đầu khi gate đang bận một lát ~70 ms | Thấp |

Giai đoạn 2 (hướng Chromium, sau khi đạt mốc Foxit): vẽ song song bằng nhiều tiến trình PDFium (PDFium
không an toàn đa luồng trong 1 tiến trình), giữ trang đã parse lâu hơn theo lịch sử xem, cache bitmap
theo tile trên đĩa.

**Chưa sửa gì trong pipeline vẽ ở đợt này**, đúng yêu cầu "đo trước khi sửa". Đề nghị: anh chạy
`Tests/PdfBench` trên file thật (và bấm giờ Foxit) rồi gửi kết quả, để chốt thứ tự trước khi làm.
