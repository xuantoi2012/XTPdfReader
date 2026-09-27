using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using XTStyle.Controls;

namespace XTPdfMergeApp
{
    /// <summary>
    /// Cửa sổ chính (cửa sổ đọc). Workspace (các file đang mở, undo/redo, cache thumbnail) vẫn do
    /// <see cref="MainWindow"/> nắm — nó giờ là cửa sổ "Ghép nhiều file" chạy ẩn và chỉ hiện khi bấm nút
    /// trên ribbon, nên 2 cửa sổ luôn thấy CÙNG danh sách file.
    /// </summary>
    public partial class ReaderShellWindow : XTCadWindow
    {
        public static ReaderShellWindow? Instance { get; private set; }

        private readonly MainWindow _workspaceWindow;

        public ReaderShellWindow(MainWindow workspaceWindow)
        {
            InitializeComponent();
            Instance = this;
            _workspaceWindow = workspaceWindow;
            Reader = ReaderWindow.GetOrCreate(workspaceWindow.Documents);
            Reader.EditHost = workspaceWindow;
            ReaderHost.Content = Reader;
            Closing += Shell_Closing;
        }

        public ReaderWindow Reader { get; }

        /// <summary>Đưa cửa sổ đọc lên trước (vd double-click 1 trang trong cửa sổ ghép).</summary>
        public void BringToFront()
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        private void Shell_Closing(object? sender, CancelEventArgs e)
        {
            Reader.ShutdownReader();
            _workspaceWindow.CloseForShutdown();
        }

        private void Shell_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = GetDroppedPdfs(e).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private async void Shell_Drop(object sender, DragEventArgs e)
        {
            var files = GetDroppedPdfs(e);
            if (files.Length == 0) return;
            e.Handled = true;
            await _workspaceWindow.OpenFilesInReaderAsync(files);
        }

        private static string[] GetDroppedPdfs(DragEventArgs e)
            => e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths
                ? paths.Where(p => string.Equals(Path.GetExtension(p), ".pdf", StringComparison.OrdinalIgnoreCase)).ToArray()
                : Array.Empty<string>();
    }
}
