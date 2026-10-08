# Đóng gói & phát hành PDF Reader Pro

Dùng [Velopack](https://velopack.io) để đóng gói: `Setup.exe` tự cài (tạo shortcut Desktop +
Start Menu, đăng ký Add/Remove Programs), và app tự kiểm tra/tải bản mới khi chạy
(`Services/AppUpdateService.cs`).

## Giao diện và icon

- Logo chung: `Resources/AppIcon.png`, nhúng qua `Resources/AppBrand.xaml`. Dùng cho tiêu đề,
  Start, About, tab tài liệu và danh sách gần đây. `PDF icon.ico` dùng cho EXE và Velopack;
  `Packaging/Win11ContextMenu/PDF icon.png` dùng cho gói menu Windows.
- `Build-Release.ps1` chạy `Build-Splash.ps1 -Version ...` để render `InstallerSplash.xaml`
  thành `splash.png` và `splash.gif` 480 × 390, với phiên bản đúng của bản phát hành.
  GIF có thanh gradient xanh lá chạy dạng chờ; không hiển thị phần trăm giả.
  Velopack không cung cấp thanh tiến trình gradient tùy biến, nên tắt thanh mặc định
  bằng `--splashProgressColor None` và dùng GIF động.
- Nền dùng chung `Controls/AmbientBackdrop.xaml`: ánh sáng xanh–lavender–mint dịu,
  phủ liên tục cả header/footer. Trong app, chuyển động bằng transform WPF và dừng
  khi cửa sổ đóng; tự tắt nếu Windows tắt animation hoặc không có tăng tốc đồ họa.
  Splash có 96 frame × 100 ms, lặp 9,6 giây với nền và thanh tiến trình chuyển động.
- Icon bitmap dùng `RenderOptions.BitmapScalingMode="HighQuality"`. `Build-Icons.ps1`
  xuất PNG với logo chiếm 96% khung và ICO 16/20/24/32/40/48/64/96/128/256 px.
  Cửa sổ chính dùng ICO nhiều frame để Windows chọn theo DPI.
- Khi tải và lên lịch cập nhật thành công, `UpdateReadyWindow` hiện modeless;
  nút Đã hiểu, Escape và nút đóng chỉ đóng thông báo. Không ép thoát ứng dụng.
- Xem lại giao diện mà không cài hay cập nhật: chạy `Preview-Branding.ps1`.
  Script render XAML thực tế thành `docs/design/implemented/update-ready.png`.

## Việc làm 1 LẦN DUY NHẤT (thiết lập ban đầu)

### 1. Cài công cụ `vpk`

```powershell
dotnet tool install -g vpk
```

### 2. Tạo repo GitHub chỉ chứa bản phát hành

Giống cách XTToolbox đang làm (`XTToolbox-Releases`): tạo 1 repo **public** mới tên
`PDFReaderPro-Releases` (repo code `XTPdfReader` vẫn để private như cũ — 2 repo tách biệt,
máy người dùng tải installer từ repo public này mà không cần token GitHub gì cả).

Nếu muốn đặt tên khác, sửa `ReleasesRepoUrl` trong `Services/AppUpdateService.cs` cho khớp.

### 3. Ký số (khuyến khích, không bắt buộc)

Không ký số: `Setup.exe` vẫn chạy bình thường, nhưng Windows SmartScreen sẽ cảnh báo "Unknown
publisher" ở người cài lần đầu cho tới khi đủ nhiều người đã cài (Microsoft tự "học" dần).

Để ký số cần 1 chứng chỉ ký mã (code signing certificate), 2 lựa chọn phổ biến:

- **Chứng chỉ OV/EV truyền thống** — mua từ DigiCert/SSL.com/Sectigo (vài trăm $/năm), dùng với
  `signtool.exe` (có sẵn trong Windows SDK).
- **Azure Trusted Signing** — dịch vụ ký số theo tháng của Microsoft, rẻ hơn, không cần giữ file
  chứng chỉ vật lý. `vpk pack` có hỗ trợ thẳng qua `--azureTrustedSignFile`.

Có chứng chỉ rồi thì truyền vào lúc đóng gói — xem ví dụ ở dưới.

## Mỗi lần phát hành 1 bản mới

### 1. Đóng gói

```powershell
cd Packaging\Release
.\Build-Release.ps1 -Version 1.0.1
```

Có chứng chỉ ký số (truyền thống qua `signtool.exe`):

```powershell
.\Build-Release.ps1 -Version 1.0.1 -SignToolParams '/a /fd sha256 /t http://timestamp.digicert.com'
```

Xong sẽ có `Packaging\Release\_release\PDFReaderPro-win-Setup.exe` và vài file khác
(`releases.win.json`, `*.nupkg`...) — **cần đưa CẢ THƯ MỤC `_release`**, không chỉ file
Setup.exe, vì app tự cập nhật đọc các file `releases.win.json`/`*.nupkg` đó để tính bản vá
(delta) nhỏ thay vì bắt tải lại nguyên bộ cài mỗi lần.

### 2. Đưa lên GitHub Releases

Trên repo `PDFReaderPro-Releases`, tạo 1 Release mới gắn tag `v1.0.1` (đúng số bản vừa đóng gói),
rồi kéo-thả TOÀN BỘ file trong thư mục `_release` vào làm asset đính kèm. Có thể làm tay trên
GitHub web, hoặc bằng `gh` CLI:

```powershell
gh release create v1.0.1 (Get-ChildItem _release\* | ForEach-Object FullName) `
    --repo xuantoi2012/PDFReaderPro-Releases --title "v1.0.1"
```

Xong bước này, app đang chạy ở máy người dùng (bản cũ hơn) sẽ tự thấy và tải bản mới ở lần mở
tiếp theo — không cần làm gì thêm.

## Test trước khi phát hành thật

Trước khi đưa lên GitHub, nên tự cài thử trên máy mình:

```powershell
_release\PDFReaderPro-win-Setup.exe
```

Gỡ thử (đảm bảo gỡ sạch, không để sót file/shortcut):

```powershell
& "$env:LOCALAPPDATA\PDFReaderPro\Update.exe" --uninstall --silent
```
