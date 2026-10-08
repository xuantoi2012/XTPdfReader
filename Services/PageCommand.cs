namespace XTPdfMergeApp.Services;

/// <summary>Lệnh trên các trang đang chọn của panel Pages (menu chuột phải, phím tắt, hàng thao tác).</summary>
internal enum PageCommand
{
    Copy, Cut, PasteAfter, PasteBefore,
    MoveToStart, MoveUp, MoveDown, MoveToEnd, MoveToPosition,
    Duplicate, RotateLeft, RotateRight, Extract, Delete,
    /// <summary>OCR of the chosen pages (the text is kept until Save).</summary>
    Ocr,
    /// <summary>Read sheet info of the chosen pages.</summary>
    ReadSheetInfo
}
