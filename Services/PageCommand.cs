namespace XTPdfMergeApp.Services;

/// <summary>Lệnh trên các trang đang chọn của panel Pages (menu chuột phải, phím tắt, hàng thao tác).</summary>
internal enum PageCommand
{
    Copy, Cut, PasteAfter, PasteBefore,
    MoveToStart, MoveUp, MoveDown, MoveToEnd, MoveToPosition,
    Duplicate, RotateLeft, RotateRight, Extract, Delete
}
