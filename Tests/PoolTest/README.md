# PoolTest — kiểm tra pool K bản PDFium (bước 3)

Lệnh này chạy **đúng code** `Services/PdfThumbnailService*.cs`, `PdfiumInstance.cs` (pool) và `PdfFileBuffer.cs`
của app. Vài kiểu WPF được thay bằng `WpfStubs.cs`, nên không cần mở cửa sổ app.

```powershell
cd Tests\PoolTest
copy "P:\...\file-nho.pdf" "$env:TEMP\nho.pdf"      # test đổi mtime của file nhỏ → dùng BẢN SAO
dotnet run -c Release -- 1 "$env:TEMP\nho.pdf" "P:\...\03. QUYEN 2.2 ... TCTC.pdf" 20
dotnet run -c Release -- 4 "$env:TEMP\nho.pdf" "P:\...\03. QUYEN 2.2 ... TCTC.pdf" 20
```

Tham số lần lượt là: K (số bản PDFium), file nhỏ, file lớn, số giây stress.

Kết quả phải là `ALL PASS`. Các mục được kiểm tra:
- Vẽ đồng thời cho ảnh giống hệt vẽ tuần tự; tile batch cũng vậy.
- Việc được chia cho đủ K bản.
- Trang đã parse được dùng lại.
- `SuspendDocumentAsync` (khi ghi file) đóng document ở mọi bản.
- Tắt layer thì mọi bản vẽ theo trạng thái mới.
- Stress 16 luồng có huỷ ngẫu nhiên, tile, `ReleaseCachedPages`, `Suspend`: không lỗi, xong việc thì tải của
  mọi bản về 0.
- Shutdown sạch.

Dòng `12 trang file lớn cùng lúc: … ms` là số để so tốc độ giữa K=1 và K=4.
