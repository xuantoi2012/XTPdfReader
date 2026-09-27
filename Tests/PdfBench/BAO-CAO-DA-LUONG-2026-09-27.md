# Vẽ song song cho cuộn liên tục: kiến trúc đề xuất (mục 3, chưa code app)

## 1. Tóm tắt

- **Đề xuất:** không dùng tiến trình worker. Thay vào đó nạp **N bản sao `pdfium.dll` (khác tên file) trong
  cùng tiến trình app**, mỗi bản sao chạy trên 1 luồng.
- **Vì sao được:** mỗi bản sao là 1 module riêng với trạng thái toàn cục riêng, nên chạy song song thật như N
  tiến trình. Các bản sao dùng chung **đúng 1 `PdfBlockCache`**, và chỉ có 1 nơi đọc mạng.
- **Phần khó nhất biến mất:** chia sẻ bộ đệm qua ranh giới tiến trình không còn cần nữa, vì bộ đệm vốn đã
  nằm chung 1 tiến trình.
- **Tốc độ đo được (máy thử, chưa phải file thật):** bằng hoặc hơn N tiến trình.
  - Dữ liệu đã có trong bộ đệm: 4 bản cho **×3,8**, so với ×3,5 của 4 tiến trình.
  - Bộ đệm còn trống: **×1,67**, so với ×0,73 (chậm hơn) của N tiến trình tự đọc riêng.
- **Đúng-sai:** ảnh do N bản vẽ song song giống hệt từng bit ảnh do 1 bản vẽ (100/100 trang).
- **RAM:** mỗi bản thêm khoảng 20 MB.
- **Việc phải làm trước khi code:** chạy `scroll` trên Windows thật để xác nhận Windows nạp các bản sao
  thành module riêng. Nếu không được, quay về kiến trúc đa tiến trình (mục 3) — kiến trúc đó vẫn khả thi.

## 2. Số đo

### File thật, ổ P:, máy 16 nhân (N tiến trình, mỗi worker tự đọc riêng)

| | 1 tiến trình | N tiến trình |
|---|---:|---:|
| RAM, 3 trang/màn | 752–826 ms | 355–370 ms (×2,0–2,3) |
| RAM, 2 trang/màn | 428–434 ms | 242–266 ms (×1,6–1,8) |
| net, 3 trang/màn | 993–1020 ms | 560–582 ms. Chờ đọc chiếm 72–74% thời gian, đọc 1758 KB/trang (1 tiến trình: 887 KB/trang) |

### Máy thử, 4 nhân (`scroll`, có thêm kịch bản "N bản PDFium/1 tiến trình")

File tổng hợp có kiểu đọc giống file thật, giả lập mạng 6 ms/21 MB/s.

| Đủ ảnh nét, 1 màn | 1 tiến trình | N tiến trình | N bản PDFium/1 tiến trình |
|---|---:|---:|---:|
| RAM, 3 trang | 258 ms | 102 ms (×2,5) | **90 ms (×2,9)** |
| RAM, 4 trang, 25 màn | 800 ms | 228 ms (×3,5) | **211 ms (×3,8)** |
| net, 3 trang | 385 ms | 529 ms (×0,73) | **230 ms (×1,67)**, đọc 905 KB/trang |

Ở dòng net, "N bản PDFium" đã bật việc không đọc trùng khối đang đọc dở. Việc này hiện chỉ có trong bench,
chưa có trong app (xem 4.2). Khi tắt nó đi: đọc 1604 KB/trang và chỉ nhanh hơn ×1,27. Lý do: các luồng
cùng lúc cần cùng khối (xref, block dùng chung), và `PdfBlockCache` hiện tại để mỗi luồng tự đọc lại khối đó.

## 3. Hai kiến trúc

### A. Đa tiến trình kiểu Chrome

- **Dữ liệu file:** app chính giữ bộ đệm trong vùng nhớ chia sẻ có tên. Worker chỉ map vùng đó ở chế độ **chỉ
  đọc**, giống `ReadOnlySharedMemoryRegion` của Chrome: tiến trình trình duyệt tải dữ liệu, còn tiến trình
  chạy PDFium chỉ đọc.
- **Khối còn thiếu:** worker gửi "cần khối k" qua pipe; app đọc khối đó (vẫn ưu tiên trang đang xem) rồi báo
  lại.
- **Cờ "khối đã có":** nằm trong vùng nhớ chung, app ghi và worker đọc, cần đúng thứ tự ghi/đọc bộ nhớ giữa 2
  tiến trình.
- **Ảnh:** trả về qua vùng nhớ chia sẻ riêng cho từng worker.
- **Các việc phải thêm:**
  - 1 project `.exe` worker mới.
  - Giao thức lệnh/huỷ/ưu tiên qua pipe.
  - Khởi động lại worker khi nó chết.
  - Báo đóng/mở document (khi ghi file, khi đổi layer) sang từng worker.
  - Chép ảnh thêm 1 lần (đo được khoảng 2,7 ms/trang).
- **Lợi thế duy nhất so với B:** PDFium crash ở worker thì không kéo sập app.
- Chrome chỉ chạy 1 PDFium cho mỗi document. Chrome không vẽ song song nhiều trang của cùng 1 file, nên với A
  ta đi xa hơn cách Chrome làm.

### B. N bản PDFium trong 1 tiến trình (đề xuất)

- **Bản sao DLL:** lúc build, chép `pdfium.dll` thành `pdfium_1.dll`, …, `pdfium_{K-1}.dll` cạnh app. Không
  chép ra thư mục tạm lúc chạy như bench đang làm.
- **Gọi hàm:** mỗi bản sao có 1 bảng hàm (`NativeLibrary.Load` + con trỏ hàm), thay cho các `[DllImport("pdfium")]`
  hiện có. PdfBench đã có sẵn bảng hàm này: `PdfiumCopies.cs`.
- **Bộ đệm:** vẫn là `PdfBlockCache` hiện tại. Mỗi bản sao mở document của riêng nó trên cùng bộ đệm; cơ chế
  đếm tham chiếu đã hỗ trợ việc này.
- **Không có IPC**, không chép ảnh thêm lần nào, không có worker phải quản lý.
- **Nhược điểm:** PDFium crash thì app sập, nhưng hiện tại cũng đã như vậy.

**So sánh:**

| | A. Đa tiến trình | B. N bản/1 tiến trình |
|---|---|---|
| Tốc độ | như B, trừ ~3 ms/trang chép ảnh | như A hoặc hơn |
| Chia sẻ `PdfBlockCache` | khó: vùng nhớ chung + cờ + giao thức đọc khối | có sẵn |
| Khối lượng code (ước lượng) | 2–3 lần B | ~600–900 dòng, chủ yếu trong `PdfThumbnailService` |
| Crash PDFium | chỉ worker chết | app chết (như hiện tại) |
| Rủi ro chính | độ phức tạp IPC, vòng đời worker | loader Windows có nạp bản sao thành module riêng không (**phải đo trên Windows**) |

## 4. Thiết kế B

### 4.1. Các thành phần

- **`PdfiumInstance`**
  - Chứa bảng hàm của 1 bản PDFium.
  - Có **gate riêng**: gate toàn cục hiện tại trở thành gate của từng bản, vì trong 1 bản PDFium vẫn không
    được gọi song song.
  - Có danh sách lease document riêng.
- **Số bản K:** `clamp(số nhân / 2, 1, 4)`. Bản 0 là bản gốc. Máy 16 nhân được 4 bản, tốn thêm khoảng 60 MB
  RAM.
- **Điều phối trang:**
  - Trang giao cho bản đang rảnh, và ưu tiên bản đã từng parse trang đó (dùng lại page handle — hot pages #3,
    giờ tính theo từng bản).
  - Hàng đợi ưu tiên Visible > Thumbnail > Background giữ như cũ.
  - Việc nhường khi zoom/pan (#5) áp dụng trong từng bản.
- **Lease theo (file, bản PDFium):**
  - Mỗi bản tự parse xref lần đầu, khoảng 21 ms/bản, làm khi cần.
  - Layer: mọi bản dùng chung 1 phần nối thêm (`BuildVisibilityTail` chạy 1 lần).
- **Ghi file** (xoay trang, annotation): `SuspendDocumentAsync` đóng lease của file đó ở **mọi** bản.
- **Lệnh không phải vẽ** (đếm trang, cỡ trang…): chạy trên bản 0.
- **File > 500 MB** (không đệm): chỉ dùng bản 0, như hiện tại, để tránh mỗi bản tự đọc mạng riêng.

### 4.2. Sửa `PdfBlockCache`

Khối đang được 1 luồng đọc thì luồng khác **chờ** luồng đó, không đọc lại. Mỗi khối có thêm trạng thái "đang
đọc". Việc này có ích ngay cả bây giờ, vì iText và PDFium có thể đọc cùng lúc. Đo trong bench: số byte đọc
mỗi trang giảm từ 1604 KB xuống 905 KB, và kịch bản net nhanh hơn từ ×1,27 lên ×1,67.

### 4.3. Thứ tự làm, đo sau mỗi bước

1. **`PdfBlockCache` không đọc trùng.** Viết test và đo lại `window` D để chắc không chậm đi.
2. **Chuyển P/Invoke sang `PdfiumInstance`, K = 1.** Chỉ đổi cấu trúc, hành vi giữ y hệt. Đo lại `bench` để
   chắc không chậm đi.
3. **Build tạo `pdfium_1..3.dll`; thêm pool bản PDFium và điều phối; K > 1.** Đây là bước rủi ro nhất (lease,
   gate, suspension, hot pages). Đo bằng `scroll`, và bấm giờ cuộn liên tục trong app thật.
4. **Bảng Debug:** hiện số việc và thời gian chờ của từng bản.

## 5. Cải thiện dự kiến trên cuộn liên tục thật

- **Trang đã có trong bộ đệm** (phần lớn thời gian khi cuộn): dựa trên số thật của N tiến trình ở chế độ RAM,
  vì B đo được bằng hoặc hơn N tiến trình.
  - 3 trang/màn: 752–826 ms → khoảng **350 ms** (×2–2,3).
  - 2 trang/màn: khoảng ×1,6–1,8.
  - Máy 16 nhân với K = 4 còn vẽ song song được cả trang tải trước (#2), nên cuộn liên tục có thể được lợi
    thêm.
- **Vừa nhảy tới vùng chưa đọc:** trên máy thử khoảng ×1,6. Số thật cần đo bằng `scroll` trên ổ P:, vì mức
  lợi ở đây phụ thuộc bao nhiêu phần là chờ mạng (phần chờ mạng không song song hoá được, đường truyền đã
  chạm trần).

## 6. Cần chạy trên Windows thật trước khi code

```powershell
cd Tests\PdfBench
dotnet run -c Release -- scroll "P:\...\03. QUYEN 2.2 ... TCTC.pdf"
dotnet run -c Release -- scroll "P:\...\03. QUYEN 2.2 ... TCTC.pdf" --visible 4
```

Mỗi lệnh 2 lần. Các dòng cần xem:
- **"N bản PDFium/1 TT — đủ ảnh nét"** và dòng **"nhanh hơn"** tương ứng.
- Câu **"giống hệt từng bit"**. Nếu thay vào đó ra **"!! … KHÁC"** hoặc chương trình crash, nghĩa là B không
  dùng được trên Windows, và ta chuyển sang A.
- **"RAM thêm khi dùng N bản PDFium"**.
