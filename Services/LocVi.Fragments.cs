using System.Collections.Generic;

namespace XTPdfMergeApp.Services
{
    /// <summary>Vietnamese for the parts of longer messages (the sentences of a status line or a notification) and for short words used in several places.</summary>
    internal static partial class LocVi
    {
        private static void AddFragments(Dictionary<string, string> w)
        {
            void A(string en, string vi) => w[en] = vi;

            // sentences of the notifications after a batch tool
            A("Ctrl+S saves it into the file", "Ctrl+S để lưu vào tệp");
            A("Undo takes it back", "Hoàn tác để lấy lại");
            A("They are painted out now", "Chúng đã được xóa khỏi hình");
            A("Ctrl+S removes them from the file, Undo brings them back", "Ctrl+S để xóa hẳn khỏi tệp, Hoàn tác để lấy lại");
            A("Ctrl+S saves them", "Ctrl+S để lưu");
            A("Undo takes them all back", "Hoàn tác để lấy lại tất cả");
            A("drag a corner to resize one", "kéo một góc để đổi cỡ");
            A("Ctrl+S saves them into the file", "Ctrl+S để lưu vào tệp");
            A("Not saved yet: Ctrl+S to save, Ctrl+Z to undo", "Chưa lưu: Ctrl+S để lưu, Ctrl+Z để hoàn tác");
            A("Ctrl+S saves it", "Ctrl+S để lưu");
            A("Saved as ", "Đã lưu thành ");

            // status of the bottom bar and panels
            A("Loading into memory", "Đang nạp vào bộ nhớ");
            A("Opening file", "Đang mở tệp");
            A("No results", "Không có kết quả");
            A("Nothing to export.", "Không có gì để xuất.");
            A("Nothing is cached right now.", "Hiện chưa có gì trong bộ đệm.");
            A("File not found", "Không tìm thấy tệp");
            A("No pages selected.", "Chưa chọn trang nào.");
            A("Select a page to view", "Chọn một trang để xem");
            A("Unsaved changes", "Có thay đổi chưa lưu");
            A("Yes", "Có");
            A("No", "Không");
            A("OK", "OK");
            A("Cancel", "Hủy");
            A("Retry", "Thử lại");
            A("Apply", "Áp dụng");
            A("Done", "Xong");
            A("Name", "Tên");
            A("Size", "Dung lượng");
            A("Date", "Ngày");
            A("Status", "Trạng thái");
            A("Author", "Tác giả");
            A("Group", "Nhóm");
            A("Scale", "Tỉ lệ");
            A("Version", "Phiên bản");
            A("Digitally signed PDF", "PDF có chữ ký số");
            A("PDF document", "Tài liệu PDF");
            A("Help", "Trợ giúp");
            A("Success", "Thành công");
            A("Error", "Lỗi");
            A("Warning", "Cảnh báo");
            A("Information", "Thông tin");
            A("Today", "Hôm nay");
            A("Yesterday", "Hôm qua");
            A("just now", "vừa xong");

            // shortcuts window
            A("Keyboard shortcuts", "Phím tắt");
            A("Find & commands", "Tìm kiếm & lệnh");
            A("Command palette", "Bảng lệnh");
            A("Full screen", "Toàn màn hình");
            A("Fit width", "Vừa chiều rộng");
            A("Previous / next page", "Trang trước / trang sau");
            A("Crosshair through the pointer (Esc turns it off)", "Đường chữ thập qua con trỏ (Esc để tắt)");
            A("Editing", "Chỉnh sửa");
            A("View", "Xem");
            A("Undo / redo page or annotation changes", "Hoàn tác / làm lại thay đổi trang hoặc chú thích");
            A("Leave the current tool / clear annotation selection", "Rời công cụ hiện tại / bỏ chọn chú thích");
            A("Crosshair: lines through the pointer, to read a drawing", "Đường chữ thập: các đường qua con trỏ, để soi bản vẽ");
            A("Crosshair off", "Tắt đường chữ thập");

            // tool panels
            A("Find in area", "Tìm trong vùng");
            A("Stamp pages", "Đóng dấu trang");
            A("Find", "Tìm");
            A("Replace checked", "Thay các mục đã chọn");
            A("Delete checked text", "Xóa chữ đã chọn");
            A("Delete checked objects", "Xóa đối tượng đã chọn");
            A("Stamp the pages", "Đóng dấu các trang");
            A("Text", "Chữ");
            A("Use this place on all sizes", "Dùng vị trí này cho mọi khổ");
            A("Add image…", "Thêm ảnh…");
            A("New text stamp…", "Dấu chữ mới…");
            A("Remove", "Xóa");
            A("Opacity", "Độ trong suốt");
            A("Pages", "Trang");
            A("Page", "Trang");
            A("Drawn object", "Đối tượng vẽ");
            A("Drawn object (signature, stamp, line, image)", "Đối tượng vẽ (chữ ký, dấu, đường, ảnh)");

            // settings hints (sentence by sentence)
            A("Keeps the largest cache and preloads up to 4 pages ahead", "Giữ bộ đệm lớn nhất và nạp trước tới 4 trang");
            A("Best for powerful PCs", "Phù hợp nhất cho máy mạnh");
            A("may use several GB of memory", "có thể dùng vài GB bộ nhớ");
            A("Keeps more recent pages and preloads up to 2 pages ahead", "Giữ nhiều trang gần đây hơn và nạp trước tới 2 trang");
            A("Uses more memory to speed up revisits", "Dùng nhiều bộ nhớ hơn để xem lại nhanh hơn");
            A("Reclaims distant pages promptly and preloads 1 page ahead", "Giải phóng nhanh các trang ở xa và nạp trước 1 trang");
            A("Recommended for keeping memory usage low", "Khuyên dùng để giữ mức dùng bộ nhớ thấp");
            A("Applies while reading", "Áp dụng khi đang đọc");
            A("all modes reduce memory use when Windows is low on memory", "mọi chế độ đều giảm dùng bộ nhớ khi Windows thiếu bộ nhớ");
            A("The virtual-printer agent reads the folder when it starts, so a change reaches it after you sign out and in (or restart the agent)", "Tác nhân máy in ảo đọc thư mục này khi khởi động, nên thay đổi sẽ có hiệu lực sau khi bạn đăng xuất rồi đăng nhập lại (hoặc khởi động lại tác nhân)");
            A("Default app", "Ứng dụng mặc định");
            A("On — double-clicking a PDF opens this app.", "Bật — nhấp đúp vào PDF sẽ mở ứng dụng này.");
            A("On — “View PDF file” in pdfFactory opens this app.", "Bật — “View PDF file” trong pdfFactory sẽ mở ứng dụng này.");
            A("Off — pdfFactory uses its own viewer.", "Tắt — pdfFactory dùng trình xem riêng của nó.");
            A("Blue", "Xanh dương");
            A("Green", "Xanh lá");
            A("Orange", "Cam");
            A("Purple", "Tím");
            A("Red", "Đỏ");

            // print dialog
            A("Looking for color pages…", "Đang tìm các trang màu…");
            A("Pages that carry color pictures are picked for you. Add or remove pages by typing; they are printed in color on the printer chosen for them.", "Các trang có ảnh màu được chọn sẵn cho bạn. Gõ để thêm hoặc bớt trang; các trang này được in màu trên máy in chọn cho chúng.");
            A("Which pages", "Những trang nào");
            A("Copies", "Số bản");
            A("Check the page range (for example 1-12, 40, 55-60).", "Hãy kiểm tra phạm vi trang (ví dụ 1-12, 40, 55-60).");
            A("Print options", "Tùy chọn in");
            A("Color pages", "Trang màu");
            A("Pages by paper size", "Trang theo khổ giấy");
            A("Printer and paper for each kind of sheet", "Máy in và giấy cho từng loại tờ");
            A("Pages that carry color pictures are picked for you. Add or remove pages by typing; they are printed in color on the printer chosen for them below.", "Các trang có ảnh màu được chọn sẵn cho bạn. Gõ để thêm hoặc bớt trang; các trang này được in màu trên máy in chọn cho chúng ở dưới.");
            A("Back to the pages with color pictures", "Về các trang có ảnh màu");
            A("For example 5, 12, 40-42", "Ví dụ 5, 12, 40-42");
            A("Black and white pages only: color pages keep their color", "Chỉ áp dụng cho trang đen trắng: trang màu giữ nguyên màu");
            A("Everything darker than light gray prints black — crisp CAD lines, no gray fill. Black and white pages only: color pages keep their color", "Mọi thứ tối hơn xám nhạt đều in đen — nét CAD sắc, không có nền xám. Chỉ áp dụng cho trang đen trắng: trang màu giữ nguyên màu");
            A("No page prints in color.", "Không có trang nào in màu.");
            A("Match automatically", "Tự chọn giấy khớp");
            A("Custom paper as big as the pages", "Giấy tùy chỉnh đúng bằng khổ trang");
            A("special size", "khổ đặc biệt");
            A("No printer is set for this size.", "Chưa chọn máy in cho khổ này.");
            A("Not printed.", "Không in.");
            A("This printer lists no usable paper.", "Máy in này không liệt kê loại giấy nào dùng được.");
            A("Special size: print it on a custom paper as big as the pages, or force it onto a paper of the printer.", "Khổ đặc biệt: in trên giấy tùy chỉnh đúng bằng khổ trang, hoặc ép vào một loại giấy của máy in.");
            A("Print each paper size on its own printer and paper", "In từng khổ giấy ra máy in và loại giấy riêng");
            A("Each paper size uses the printer and paper set on the right.", "Mỗi khổ giấy dùng máy in và giấy đặt ở bên phải.");
            A(" to look at", " cần xem lại");
            A(" left out", " bị bỏ qua");
            A(" · by paper size", " · theo khổ giấy");
            A("This driver keeps its own Collate setting: change it in Properties… (the box shows what the driver has)", "Trình điều khiển này tự giữ thiết lập Collate: hãy đổi trong Properties… (ô này hiện giá trị của trình điều khiển)");

            // panels
            A("History", "Lịch sử");
            A("Sheets", "Bản vẽ");
            A("No saves recorded in this file yet. Annotations, layer and bookmark changes and page rotation are listed here once saved.", "Tệp này chưa có lần lưu nào được ghi lại. Chú thích, thay đổi lớp và dấu trang, xoay trang sẽ được liệt kê ở đây sau khi lưu.");
            A("This document has no bookmarks.", "Tài liệu này không có dấu trang.");
            A("This printer's driver cannot collate copies", "Trình điều khiển máy in này không sắp bản in theo bộ được");
            A("Default printer", "Máy in mặc định");

            // context menus of the sheet list
            A("Copy sheet number", "Sao chép số hiệu bản vẽ");
            A("Open DWG", "Mở DWG");
            A("OCR this sheet…", "OCR bản vẽ này…");
            A("Read sheet info of this sheet…", "Đọc thông tin của bản vẽ này…");
            A("Save the version before this change as…", "Lưu phiên bản trước thay đổi này thành…");
            A("Custom", "Tùy chỉnh");

            // update window
            A("Update available", "Có bản cập nhật");
        }
    }
}
