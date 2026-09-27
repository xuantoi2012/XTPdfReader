# Chế độ Cuộn liên tục: so sánh với trình xem PDF của Chromium

Nguồn đối chiếu là mã Chromium (nhánh `main`, bản sao trên GitHub `chromium/chromium`):
- `pdf/document_layout.cc`
- `pdf/pdf_view_web_plugin.cc`
- `pdf/pdfium/pdfium_engine.cc`
- `pdf/paint_manager.cc`
- `chrome/browser/resources/pdf/viewport.ts`

## Bảng so sánh

### Bố cục trang

- **Chromium:** `DocumentLayout::ComputeOneUpLayout` dựng bố cục từ kích thước thật (point) của **mọi** trang, được đọc từ trước. Toạ độ tài liệu không phụ thuộc zoom; màn hình = tài liệu × zoom.
- **App trước đây:** mọi trang cùng chiều rộng 2200; `VirtualizingStackPanel` **ước lượng** chiều cao các trang chưa hiện.
- **App bây giờ:** `ContinuousPageLayout` tính từ khổ thật (A3 cạnh A1 nhỏ hơn đúng tỉ lệ). `ContinuousPagesPanel` đặt mọi trang đúng toạ độ đã tính.

### Khổ giấy trước khi hiện

- **Chromium:** biết kích thước mọi trang trước lần vẽ đầu.
- **App trước đây:** hiện trang với khổ mặc định, đọc xong khổ thật mới dời bố cục, nên trang **nhảy ngay sau khi mở**.
- **App bây giờ:** đọc khổ mọi trang **trước** khi gán tài liệu cho danh sách, chờ tối đa 1,5 s (`BindContinuousWhenSizesKnownAsync`).

### Zoom quanh con trỏ

- **Chromium:** `Viewport.setZoomInternal_`: vị trí = (cuộn + điểm) / zoom cũ × zoom mới. Chỉ là phép tính.
- **App trước đây:** mỗi nấc zoom gọi `UpdateLayout` 2 lần, rồi dò vị trí trang trên bố cục ước lượng; vừa chậm vừa lệch.
- **App bây giờ:** `ContinuousPagesPanel.ZoomAt` tính điểm neo trên bố cục, không đo lại phần tử.

### Cô lập zoom khỏi cuộn

- **Chromium:** `Viewport.mightZoom_` và `stop_scrolling_`: trong lúc zoom, plugin **không** phản ứng với sự kiện cuộn, để tránh nhấp nháy.
- **App trước đây:** vị trí cuộn đổi do zoom bị coi là người dùng cuộn. Hệ quả: đánh dấu "cuộn nhanh" rồi hoãn vẽ, đổi hướng tải trước, huỷ việc của trang. Trang chớp trống/mờ khi zoom.
- **App bây giờ:** `BeginContinuousZoomScroll` cô lập; `ScrollChanged` bỏ qua các thay đổi do zoom/bố cục gây ra (extent hoặc viewport đổi).

### Không hiện nội dung sai

- **Chromium:** chỉ có một ảnh khung nhìn; không bao giờ lộ nội dung của trang khác.
- **App trước đây:** phần tử trang được tái dùng khi cuộn vẫn còn tile nét của trang cũ, cho tới lượt làm mới kế tiếp.
- **App bây giờ:** tái dùng phần tử thì xoá tile ngay (`ContinuousTileCanvas_DataContextChanged`).

### Cuộn bằng chuột

- **Chromium:** 100 px mỗi nấc (Windows: 3 dòng × 33 px), tỉ lệ theo delta (bàn di chuột mượt).
- **App trước đây:** 48 px mỗi *sự kiện*, bất kể delta.
- **App bây giờ:** 100 px mỗi nấc, tỉ lệ theo delta, gọi thẳng panel để 2 sự kiện trong cùng một khung hình không ghi đè nhau. Windows đặt "cuộn 1 màn hình" thì cuộn 1 khung nhìn.

### Đổi cỡ cửa sổ

- **Chromium:** `Viewport.resize_()`: đang ở "vừa chiều rộng" thì khớp lại.
- **App trước đây:** không làm ở chế độ cuộn liên tục.
- **App bây giờ:** khớp lại khi đổi cỡ cửa sổ / kéo panel trái (`ReaderContinuousList_SizeChanged`).

### Vừa chiều rộng

- **Chromium:** tính theo chiều rộng khung nhìn cố định.
- **App trước đây:** phụ thuộc lúc thanh cuộn dọc đã hiện hay chưa, có lúc lệch 17 px và hiện thanh cuộn ngang.
- **App bây giờ:** luôn trừ sẵn chỗ thanh cuộn dọc.

### Pan (kéo tay) khi đã zoom

- **Chromium:** pixel đã vẽ được **dịch theo cuộn** (`PaintManager::ScrollRect`), chỉ vẽ phần mới lộ ra; ảnh cũ nằm yên tới khi có ảnh mới.
- **App trước đây:** pan quá 35% khung nhìn là huỷ mọi lượt vẽ, nên pan liên tục thì trống/mờ lâu.
- **App bây giờ:** vùng nét cũ vẫn nằm yên trên canvas; lượt vẽ dở làm nốt; chỉ lượt có vùng đã ra hẳn khỏi màn hình mới bị huỷ.

### Vẽ

- **Chromium:** chỉ vẽ phần trang giao khung nhìn, ở độ phân giải thiết bị, progressive (tối đa 300 ms mỗi lượt).
- **App:** ảnh 340 px → ảnh trang ≤ 2304 px → khi zoom sâu thì vẽ đúng vùng khung nhìn (#4), progressive 8 ms/lát, 4 bản PDFium song song.

## Khác biệt còn lại (cố ý giữ)

- **Zoom bằng Ctrl+lăn chuột:** Chromium nhảy theo các mức zoom cố định của trình duyệt (25, 33, 50, 67, 75, 80, 90, 100, 110, 125, 150, 175, 200, 250, 300, 400, 500%). App zoom liên tục theo từng nấc (bước `ReaderZoomStep`), mượt hơn khi xem bản vẽ.
- **Khe giữa trang:** Chromium dùng khe cố định 4 px (tài liệu) cộng bóng đổ. App dùng khe 14 × zoom (tối thiểu 4) và viền 1 px, không bóng đổ, vì bóng đổ bắt WPF vẽ lại bằng phần mềm mỗi nấc zoom.
