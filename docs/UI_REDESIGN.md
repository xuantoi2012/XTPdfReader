# XT PDF Reader — UI redesign (spec và tiến độ)

Nguồn thiết kế: artifact "XT PDF Reader — unified UI design" (https://claude.ai/artifact/Tm2R5znaGUXtycABLNA1Ny, bản 12).
Mockup sinh bằng script trong `docs/ui-mockup/` (`gen_ui.py` → `gen_ui2.py` → `gen_ui3.py`, chạy `python gen_ui3.py`; token, icon và
mọi màn hình nằm ở đó). Tài liệu này là nguồn sự thật cho các quyết định; mockup là hình dạng.

## 1. Quyết định đã chốt (28/09/2026)

| # | Quyết định |
|---|---|
| 1 | **Chỉ tiếng Anh.** Không có lựa chọn ngôn ngữ. Mọi chữ trên UI viết tiếng Anh; thuật ngữ CAD giữ nguyên (Layer, Typewriter, Highlight). Thông báo lỗi/hộp thoại trong code còn tiếng Việt sẽ dịch ở lượt "strings" (mục 7). |
| 2 | **Mặc định giao diện sáng**, có bản tối cùng bảng token. Chọn theme trong Settings. |
| 3 | **Tab file nằm dưới toolbar** (không đưa lên thanh tiêu đề). |
| 4 | **Nút chưa có chức năng thì bỏ hẳn**, thêm khi làm xong tính năng đó. |
| 5 | **Panel Pages cho việc nhỏ, cửa sổ Merge riêng cho việc gộp nhiều file.** |
| 6 | Sửa trang trong panel là **ảo cho tới khi Save** (dấu chấm cam trên tab, hỏi khi đóng). File trên đĩa không đổi trước lúc Save. |
| 7 | **Layer cùng tên được gộp** khi lưu file ghép (mặc định bật). Đã có sẵn trong `XTPdfMerger`. |
| 8 | **Kéo trang = di chuyển; giữ Ctrl = sao chép.** Kéo header cửa sổ file này lên file khác = chèn toàn bộ trang tại vạch chèn. |
| 9 | **Merge = mỗi file một cửa sổ con** nằm trong cửa sổ app (không phải cửa sổ hệ điều hành). Layout 1 · 2 song song · 3 cột · 2×2 · Free. Tối đa 4 cửa sổ hiện cùng lúc (cửa sổ dùng lâu nhất tự thu xuống dock). Cửa sổ không nhỏ hơn 320×240, không kéo/đổi cỡ ra ngoài vùng làm việc. Nhấp đúp header = phóng to/trả lại. Kéo tới mép = snap. Kéo trang ra vùng trống = tạo cửa sổ tạm ("Temp 1"). Dải Dock ở đáy chứa cửa sổ thu nhỏ; thả trang vào chip được. |
| 10 | Kéo thumbnail lên một tab và giữ ~0,5 s = chuyển sang file đó (thả vào vị trí muốn). |
| 11 | **Style riêng được phép** nếu XTStyle hạn chế. Không sửa repo XTStyle; overlay token và template nằm trong app (mục 4). |

## 2. Phạm vi

**Làm** (theo mockup): khung chính (toolbar, tab, thanh trái, panel Pages, thanh trạng thái), Pages panel đầy đủ (menu chuột phải,
copy/cut/paste, di chuyển, kéo thả, sang tab khác), Layers + saved Views + export theo View, Bookmarks, Comments, Find, Stamps, Print,
Export/Split, Settings, Start screen (Recent, Workspaces), thanh báo file đổi trên đĩa, Command palette (Ctrl+K), cửa sổ Merge mới,
Merge save options.

**Chưa làm (đã loại khỏi mockup):** Compare, Measure, ngôn ngữ tiếng Việt, OCR, chữ ký số, Chụp ảnh/Vẽ hình/Đính kèm file.

## 3. Màn hình → tiến độ

Trạng thái: `[ ]` chưa · `[~]` đang · `[x]` xong. Số theo mockup.

- [x] **P1 — Khung chính** (mockup 1, 2) — xong 28/09: toolbar icon + chữ (chỉ nút đã có chức năng), tab dưới toolbar (nút + nằm cuối dãy tab, menu chuột phải
  tab: Open in default app / Close), thanh trái có chữ (Pages, Bookmarks, Layers), panel Pages (Insert / Delete / Turn left / Turn right / Extract, thumbnail 2 cột
  ảo hoá, chip số trang, thanh "N pages selected" khi chọn ≥ 2), thanh trạng thái, token sáng/tối, icon nét mảnh. Chữ UI đã tiếng Anh.
  Khác mockup: nhãn xoay ghi "Turn left/right" (ô 53 px không đủ cho "Rotate left"); thanh công cụ cuộn ngang ẩn khi cửa sổ hẹp (chưa có menu "…");
  nút Light/Dark còn ở thanh tiêu đề cho tới khi làm Settings (P4); chữ trong thanh trạng thái/hộp thoại còn tiếng Việt (P10).
  Chưa có: chấm cam "unsaved" trên tab (P2, cần quyết định Save — xem mục 8).
- [~] **P2 — Pages panel hành vi** (mockup 5) — **P2a xong 28/09** (chấm cam + Save/Save As + hỏi khi đóng tab/cửa sổ: `WorkspaceDocument.IsDirty`,
  `DocumentSession.SaveGroupAsync`; Save ghi file tạm cùng thư mục rồi `File.Replace` với `.xtsave.bak` tạm, thành công thì xoá bản sao lưu; nếu window khác
  đang dùng trang của file thì chuyển sang Save As; sau Save lịch sử Undo bị xoá); còn P2b (menu, clipboard, Move) và P2c (kéo thả): menu chuột phải, Copy/Cut/Paste before-after, Move (to start / up / down / to end / to position),
  Duplicate, phím tắt (Ctrl+C/X/V/D, Alt+↑↓, Ctrl+Shift+Home/End), kéo thả có vạch chèn + tự cuộn, kéo sang tab khác (giữ 0,5 s),
  Ctrl = copy, dirty dot + hỏi khi đóng.
- [ ] **P3 — Layers** (mockup 3, 3b): cây có checkbox 3 trạng thái, ô tìm, Show all/Hide all, chip "N files" cho layer gộp, saved Views
  (lưu/áp dụng/isolate/reset), nút "Export PDF with this view…". Bookmarks panel theo cùng style.
- [ ] **P4 — Settings** (mockup 4): Appearance (Theme Light/Dark/System, accent 4 màu), Display (view mode mặc định, zoom khi mở),
  Performance & memory (files kept warm, file cache: dung lượng + Clear cache), Integration (pdfFactory).
- [ ] **P5 — Start screen, thông báo, Command palette** (mockup 13, 16): Recent (ghim), Quick locations, Workspaces (lưu/khôi phục bộ file + layout Merge),
  thanh vàng "file changed on disk" (Reload / Ignore), Ctrl+K.
- [ ] **P6 — Merge window mới** (mockup 6, 7): cửa sổ con mỗi file, layout + snap + giới hạn, Dock, cửa sổ tạm, kéo header chèn cả file,
  Move/Ctrl = Copy, Undo/Redo, Save all. Thay `MergeWorkspaceWindow`.
- [ ] **P7 — Merge save options** (mockup 17): tên file, thư mục, bookmark theo file nguồn, giữ bookmark cũ, gộp layer, số trang, tối ưu dung lượng.
- [ ] **P8 — Print và Export/Split** (mockup 14, 15): in (khoảng trang, khổ giấy, tỉ lệ, màu, theo View), Export (flatten theo View, tối ưu dung lượng,
  tách theo khổ giấy / N trang / bookmark).
- [ ] **P9 — Comments, Find, Stamps** (mockup 8, 11, 12).
- [ ] **P10 — Strings**: dịch mọi thông báo/hộp thoại trong code sang tiếng Anh; gom vào `Resources/Strings.xaml` hoặc lớp hằng.

## 4. Cách hiện thực

- **Không sửa XTStyle.** `XTButton`, `XTWindow`, `XTContextMenu`… vẫn dùng. Cái XTStyle không có thì làm ở app.
- **Token:** `Resources/UiTokens.Light.xaml`, `UiTokens.Dark.xaml` ghi đè các brush `XT*` (nền, viền, chữ, hover, selection) bằng giá trị của mockup
  và thêm brush riêng `Ui.*`. `ThemeService.Apply` gộp token SAU dictionary Light/Dark của XTStyle nên mọi control XT trong app tự nhận màu mới.
- **Icon:** `Resources/UiIcons.xaml` — `Geometry` nét mảnh (lưới 24×24, nét 1,7 px, đầu tròn) chuyển từ mockup. Template icon vẽ bằng
  `Path` với `Stroke` (khác icon Material tô đặc của `PdfUi.*`). Icon Material cũ giữ ở chỗ chưa đổi.
- **Style:** `Resources/UiStyles.xaml` — `UiToolbarButton` (icon 20 + chữ 11 px, 60×48), `UiRailButton`, `UiPanelAction`, `UiIconButton`, `UiPrimaryButton`,
  `UiTabItem`, `UiThumbCard`, `UiChip`. Trạng thái bật của công cụ vẫn là `Tag="Active"` (code-behind hiện có không đổi).
- **Code-behind:** giữ nguyên `x:Name` và handler trong `ReaderWindow*`; đổi chỗ đặt/hình dạng, không đổi logic. Panel Pages phát sự kiện
  (`InsertPagesRequested`…) để `ReaderWindow` gọi handler cũ.
- **Cỡ:** toolbar cao 60, tab cao 36, thanh trái rộng 64, panel 288 (kéo được, 240–480), thanh trạng thái 30. Cỡ chữ 12,5 (nhãn nhỏ 11, tối thiểu).
- **Màu (sáng):** nền `#F1F4F8`, mặt `#FFFFFF`, panel `#F7F9FB`, viền `#DBE1E8`, chữ `#1B2733`, chữ phụ `#566373`, hover `#E9EEF4`,
  vùng xem `#DFE5EC`, accent mặc định `#2563EB`. **Màu (tối):** `#171A1F` / `#20242B` / `#1C2026` / `#2F353E` / `#E8EBF0` / `#A3ACB9` /
  `#2A3039` / `#0F1114`, accent `#6EA8FF`. Accent chọn được: `#2563EB`, `#0F8B6D`, `#7C4DFF`, `#D9463B`.
- **Layout Merge:** vùng làm việc là `Canvas` tự quản Z-order; cửa sổ con là `UserControl` có header kéo được, resize thumb, snap; kéo thả
  dùng cùng `PagePlacement`/lệnh `IWorkspaceCommand` hiện có (`MovePagesCommand` có sẵn move và copy).

## 5. Hành vi chi tiết cần nhớ

- Toolbar: `Hand · Undo · Redo | Continuous · Fit page · Fit width · Rotate left · Rotate right | Typewriter · Note · Highlight · Stamp | … | Find · Print | Merge files`.
  Chỉ những nút đã có chức năng mới hiện. Xoay trong toolbar là xoay **khung nhìn**; xoay trong panel Pages là xoay **thật** (lưu file).
- Tab: chấm cam = có thay đổi chưa lưu. Chuột phải: Save, Close, Close others, Open in default app, Show in folder.
- Menu chuột phải trang (Pages): Copy · Cut · Paste after (Ctrl+V) · Paste before (Ctrl+Shift+V) · Move ▸ (Move to start Ctrl+Shift+Home ·
  Move up one Alt+↑ · Move down one Alt+↓ · Move to end Ctrl+Shift+End · Move to position…) · Duplicate (Ctrl+D) · Rotate left/right ·
  Extract… · Send to Merge window · Delete N pages (Del).
- Layer: layer cùng tên từ nhiều file hiện một dòng, chip "N files". View = tổ hợp bật/tắt layer lưu theo file; Export/Print có tùy chọn dùng View.
- Find: phải nói rõ khi trang không có chữ tìm được ("128 of 281 pages have no searchable text — text drawn as lines").
- Cảnh báo file đổi trên đĩa: nút Reload / Ignore; tham chiếu `PdfFileBuffer` invalidate `Changed` đã có.

## 6. Hiệu năng (không được làm xấu đi)

Đổi giao diện không được làm chậm cuộn/zoom: không thêm việc trên UI thread lúc cuộn, thumbnail vẫn ảo hóa (`VirtualizingWrapPanel`),
không giữ thêm ảnh. Kiểm tra bằng `Tests/PdfBench/CompareFoxit.ps1 -Continuous` trước và sau mỗi giai đoạn (mốc hiện tại: End 0,25–0,32 s,
zoom vào ~1,0 s, cuộn 40 nấc ~0,05 s, RAM khi dùng 620–660 MB private WS).

## 7. Ghi chú
- Chữ trong code (MessageBox, log, DiagnosticsReport) hiện tiếng Việt; P10 sẽ dịch. Bảng Debug (F12) có thể giữ tiếng Việt vì là công cụ dev.
- Sau mỗi giai đoạn: build, chạy `PoolTest`, chụp màn hình so với mockup, cập nhật checkbox ở đây.

## 8. Câu hỏi mở (cần chốt trước khi làm giai đoạn liên quan)
- **P2 — Save của sửa ảo:** Ctrl+S ghi đè file gốc (kèm bản sao lưu?) hay luôn Save As? Đề xuất: Save ghi đè có `.bak` tạm, Save As cho tên mới; hỏi khi đóng tab nếu chưa lưu.
- **P2 — Insert ▾:** menu con (from open file / from disk / blank page) — hiện nút Insert chỉ chèn từ file PDF khác.
