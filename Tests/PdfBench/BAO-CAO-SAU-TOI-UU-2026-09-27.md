# Kết quả tối ưu #0 → #5 (2026-09-27)

Tất cả số liệu dưới đây đo bằng `Tests/PdfBench` (cùng bản PDFium 147.0.7690 với app) trên file tổng hợp
159 MB (220 trang A1, 8 layer, ~60.000 nét/trang). "netsim" nghĩa là giả lập ổ mạng bằng
`--netsim 5 60`: mỗi lần PDFium nhảy vị trí đọc tốn 5 ms, băng thông 60 MB/s.

Đây là số của **riêng PDFium và các chiến lược điều phối**, chưa có phần WPF. Bấm thử trên file thật trên
ổ P: để xác nhận, rồi so với Foxit.

## Số đo từng bước

| Bước | Đo cái gì | Trước | Sau |
|---|---|---:|---:|
| **#0** Đọc cả file vào RAM (đọc nền) | Parse trang mới khi cuộn (netsim) | 49 ms | **31 ms** |
|  | Parse trang 1 lạnh (netsim) | 180 ms | **33 ms** (khi buffer đã sẵn) |
|  | Mở file → trang đầu (netsim) | 459 ms | **459 ms**: không đổi, vì cố ý không chờ đọc hết file |
| **#1** Ảnh thấp trước, ảnh nét sau | Trang mới hiện sau | 223 ms | **117 ms** (ảnh nét xong ở 306 ms) |
| **#2** Tải trước theo hướng cuộn | Trang trống TB khi cuộn 1 trang/400 ms | 117 ms | **13 ms** |
|  | … khi cuộn 1 trang/250 ms (trước đây vẽ không kịp, dồn việc) | 652 ms | **51 ms** |
| **#3** Giữ page handle theo vùng xem | Hiện lại trang vừa bị đẩy khỏi cache (netsim) | 242 ms | **188 ms** |
| **#4** Zoom sâu: 1 vùng/trang thay tile 640 px | Phủ đủ vùng xem, zoom 200% | 185 ms | **75 ms** |
|  | … zoom 400% | 119 ms | **47 ms** |
| **#5** Thumbnail nhường gate khi zoom/pan | Vùng zoom chờ gate, TB (netsim) | 18 ms | **4 ms** (max ~30 ms không đổi) |

Nguồn của từng dòng trong output PdfBench:
- #0: mục 1 và 3, chạy thường và chạy `--mem`.
- #1, #2, #3: mục 5.
- #4: mục 2d.
- #5: mục 6.

Dòng #2 là **mô phỏng dòng thời gian** trên 1 luồng PDFium, dùng chi phí parse/vẽ đo thật. Tương tác
thật của WPF chưa đo được trên Linux.

## Lưu ý và giới hạn

- **#0, lần mở lạnh:** chờ đọc hết 159 MB qua netsim mất 2,9 s mới có trang đầu, trong khi mở kiểu cũ chỉ
  0,46 s. Vì vậy app mở file kiểu cũ trước, đọc cả file ở nền, xong thì chuyển sang bản trong RAM.
  Chuyển không huỷ lệnh vẽ đang chạy.
  - Giới hạn: file > 500 MB không đọc vào RAM; tổng RAM cho các file ≤ min(2 GB, ¼ RAM máy).
  - Buffer bị bỏ khi file sắp bị ghi (xoay trang, annotation), khi file đổi trên đĩa (độ dài/mtime), hoặc
    khi đóng file.
  - Bảng "Debug tải PDF" hiện thời gian đọc nền và số MB đang giữ.
- **File tổng hợp chỉ có ~3 lần đọc/trang**, khối lớn, nên lợi ích của #0 ở đây nhỏ. File thật đo được
  parse 43–449 ms (max 1,6 s) mỗi trang, nghĩa là mỗi trang đọc nhiều lần hơn nhiều. Mục **"0. Kiểu đọc
  I/O"** của PdfBench đếm chính xác con số đó trên file thật.
- **#4:** mảnh ảnh nét đầu tiên giờ đến cùng cả vùng (~75 ms) thay vì tile đầu (~22 ms). Trong lúc chờ,
  ảnh trang độ phân giải thấp vẫn nằm bên dưới, và vùng cũ giữ trên màn hình tới khi vùng mới hiện xong.
  Pan trong phạm vi lề (12,5% vùng xem, ≥128 px) không phải vẽ lại.
- **#5:** thumbnail đã bắt đầu vẽ thì được vẽ nốt, không dừng giữa chừng khi đang giữ khoá trang (nếu dừng,
  vùng zoom của chính trang đó sẽ phải chờ). Vì vậy max chờ không giảm.
- Đã kiểm chứng PDFium chạy an toàn khi xen kẽ vẽ progressive của các trang khác nhau, kể cả khác luồng.
  Pipeline hiện tại của app dựa vào điều này.

## Đề nghị đo trên file thật (ổ P:)

```powershell
cd Tests\PdfBench
dotnet run -c Release -- bench "P:\...\03. QUYEN 2.2 ... TCTC.pdf"        # như app trước #0
dotnet run -c Release -- bench "P:\...\03. QUYEN 2.2 ... TCTC.pdf" --mem  # parse từ RAM (sau #0)
```

Mỗi lệnh chạy 2–3 lần. Mục 0 (số lần đọc/trang) và mục 5–6 (từng bước) là những số quan trọng nhất.
Sau đó mở app thật, xem bảng Debug, và bấm giờ so với Foxit: mở → trang đầu, zoom 400% → nét, cuộn
20 trang.

## Cập nhật: #0 đổi sang bộ đệm khối (phương án C)

Đo trên file thật 163 MB ở ổ P: (`readtest`, `window --block 256 --bgkb 256`, mỗi lệnh 2 lần):
- Đọc tuần tự 16–26 MB/s với mọi cỡ khối (1/4/16 MB) và số luồng (1/2/4): đã chạm trần băng thông, đọc
  song song không có lợi.
- Parse trang mới trong lúc đọc nền:

| Kịch bản | TB | Trung vị | Max |
|---|---:|---:|---:|
| A. Trước #0 (không đọc nền) | 1041–1154 ms | 1184–1238 | 1785–2038 |
| B. #0 cũ (đọc hết file nền, rồi `FPDF_LoadMemDocument64`) | 1637–1739 ms | 1026–1070 | 11145–12022 |
| C. Bộ đệm khối 256 KB, trang ưu tiên | **116–127 ms** | 71–103 | 552–652 |

Vì vậy #0 giờ là: PDFium luôn mở file qua `FPDF_LoadCustomDocument` trên `PdfBlockCache`:
- Khối trang cần thì đọc ngay; luồng nền nạp các khối còn thiếu, mỗi lượt 256 KB, và đứng chờ khi trang
  đang chờ khối.
- Không còn bước "đọc xong → đổi sang document trong RAM", nên cũng không phải parse lại trang đang xem.
- iText (bật/tắt layer) cũng đọc qua bộ đệm này.
- File > 500 MB hoặc hết ngân sách RAM: `FPDF_LoadDocument` như cũ.

Kịch bản **D** của `window` chạy đúng code này của app. Trên máy thử (giả lập 6 ms/21 MB/s, file
tổng hợp có kiểu đọc giống file thật), D = TB 100 ms, trung vị 70, max 571 ms, khớp C. Mở file 57 ms,
so với 60 ms khi đọc kiểu cũ. Cần chạy lại trên ổ P: để xác nhận.

