using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using XTPdfMergeApp.Services;
using XTPdfMergeApp.Workspace;
using PageRow = XTPdfMergeApp.Domain.PagePlacement;
using DocumentGroup = XTPdfMergeApp.Domain.WorkspaceDocument;

namespace XTPdfMergeApp
{
    /// <summary>Các thao tác chỉnh sửa nhanh gọi từ toolbar Viewer (xoay trang lưu file, chèn trang
    /// từ file khác, xuất trang) — Viewer chỉ biết trang đang xem, MainWindow nắm selection của
    /// Organizer, workspace/undo và cache thumbnail nên thực thi ở đây.</summary>
    public partial class MainWindow : IReaderPageEditHost
    {
        /// <summary>Mọi lần ghi đè file nguồn đi tuần tự — Undo/Redo bấm liên tục không được ghi chồng
        /// lên nhau trên cùng 1 file.</summary>
        private static readonly SemaphoreSlim _sourceEditGate = new(1, 1);

        IReadOnlyList<PageRow> IReaderPageEditHost.GetSelectedPages(DocumentGroup group)
        {
            var listBox = FindPageListBoxFor(group);
            if (listBox == null) return Array.Empty<PageRow>();
            return listBox.SelectedItems.Cast<PageRow>()
                .Where(p => group.Pages.Contains(p))
                .OrderBy(p => group.Pages.IndexOf(p))
                .ToList();
        }

        // ── Xoay trang THẬT (lưu file) ──────────────────────────────────────

        async Task IReaderPageEditHost.RotatePagesAsync(IReadOnlyList<PageRow> pages, int deltaDegrees)
        {
            var targets = pages
                .GroupBy(p => p.SourcePath, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Path: g.Key, Pages: (IReadOnlyCollection<int>)g.Select(p => p.PageNumber).Distinct().ToList()))
                .ToList();
            if (targets.Count == 0) return;

            if (!await ApplySourceRotationAsync(targets, deltaDegrees)) return;
            _workspace.History.Record(new RotateSourcePagesCommand(deltaDegrees,
                targets.Sum(t => t.Pages.Count), delta => ApplySourceRotationAsync(targets, delta)));
        }

        private Task<bool> ApplySourceRotationAsync(
            IReadOnlyList<(string Path, IReadOnlyCollection<int> Pages)> targets, int deltaDegrees)
            => EditSourceFilesAsync(targets, (path, pages) => PdfPageEditService.RotatePages(path, pages, deltaDegrees),
                geometryChanged: true);

        // ── Annotation (Typewriter / Ghi chú / Highlight) ─────────────────

        async Task IReaderPageEditHost.ApplyAnnotationChangesAsync(string path, IReadOnlyList<QuickAnnotationChange> changes, string description)
        {
            if (changes.Count == 0 || !await ApplyAnnotationChangesCoreAsync(path, changes)) return;
            var inverse = changes.Reverse().Select(c => c.Inverse()).ToList();
            _workspace.History.Record(new SourceFileEditCommand(description,
                () => ApplyAnnotationChangesCoreAsync(path, changes),
                () => ApplyAnnotationChangesCoreAsync(path, inverse)));
        }

        private Task<bool> ApplyAnnotationChangesCoreAsync(string path, IReadOnlyList<QuickAnnotationChange> changes)
        {
            IReadOnlyCollection<int> pages = changes.Select(c => c.PageNumber).Distinct().ToList();
            return EditSourceFilesAsync(new[] { (path, pages) },
                (p, _) => PdfPageEditService.EditInPlace(p, doc => PdfQuickAnnotationService.ApplyChanges(doc, changes)),
                geometryChanged: false);
        }

        // ── Xoá trang khỏi window (workspace, có Undo; Lưu mới ghi ra file) ──

        void IReaderPageEditHost.DeletePages(DocumentGroup group, IReadOnlyList<PageRow> pages)
        {
            if (pages.Count == 0 || !_groups.Contains(group)) return;
            _workspace.Execute(new RemovePagesCommand(_workspace, group, pages));
            if (!_groups.Contains(group)) ReaderWindow.Instance?.NotifyGroupRemoved(group);
            else ReaderWindow.Instance?.NotifyPagesChanged(group);
            ReleaseUnusedPdfDocuments();
            UpdateStatusBar();
        }

        /// <summary>Ghi đè từng file nguồn: đóng handle PDFium → sửa bằng iText (thread nền) → mở khoá →
        /// xoá mọi bitmap cũ của các trang đó để thumbnail/Viewer render lại ngay.</summary>
        internal async Task<bool> EditSourceFilesAsync(
            IReadOnlyList<(string Path, IReadOnlyCollection<int> Pages)> targets,
            Action<string, IReadOnlyCollection<int>> edit, bool geometryChanged)
        {
            await _sourceEditGate.WaitAsync();
            try
            {
                foreach (var (path, pages) in targets)
                {
                    try
                    {
                        using (await PdfThumbnailService.SuspendDocumentAsync(path, TimeSpan.FromSeconds(3)))
                            await Task.Run(() => edit(path, pages));
                    }
                    catch (Exception ex)
                    {
                        InvalidateSourcePageRenders(path, pages, geometryChanged);
                        MessageBox.Show(this, $"Không ghi được file:\n{path}\n\n{ex.Message}", "Sửa PDF",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                        return false;
                    }
                    InvalidateSourcePageRenders(path, pages, geometryChanged);
                }
                return true;
            }
            finally
            {
                _sourceEditGate.Release();
            }
        }

        /// <summary>File nguồn vừa đổi nội dung — bỏ thumbnail/ảnh Viewer/tỉ lệ trang đang giữ của đúng
        /// các trang đó (mọi placement trỏ tới chúng, ở mọi window) rồi yêu cầu render lại.
        /// <paramref name="geometryChanged"/> = false (chỉ thêm annotation): giữ ảnh Viewer cũ tới khi ảnh
        /// mới render xong để trang không chớp trắng.</summary>
        internal void InvalidateSourcePageRenders(string path, IReadOnlyCollection<int> pages, bool geometryChanged)
        {
            bool Matches(string p, int page) =>
                pages.Contains(page) && string.Equals(p, path, StringComparison.OrdinalIgnoreCase);

            Interlocked.Increment(ref _thumbnailGeneration);
            lock (_thumbnailLock)
            {
                _thumbnailCache.RemoveWhere(key => Matches(key.Path, key.Page));
                foreach (var key in _thumbnailLoads.Keys.Where(key => Matches(key.Path, key.Page)).ToList())
                    _thumbnailLoads.Remove(key);
            }

            var affected = _groups.SelectMany(g => g.Pages).Where(p => Matches(p.SourcePath, p.PageNumber)).ToList();
            foreach (var row in affected)
            {
                row.Thumbnail = null;
                row.ThumbnailLoadQueued = false;
                if (!geometryChanged) continue;
                row.ReaderBitmap = null;
                row.ReaderBitmapLoadQueued = false;
                row.AspectRatio = null;
                row.AspectRatioLoadQueued = false;
            }

            ReaderWindow.Instance?.OnSourcePagesEdited(path, pages, geometryChanged);
            foreach (var row in affected) _ = LoadThumbnailFor(row);
            _ = Dispatcher.InvokeAsync(QueueVisibleThumbnailScans, System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        // ── Chèn trang từ file khác ────────────────────────────────────────

        async Task IReaderPageEditHost.InsertPagesFromFileAsync(DocumentGroup target, int insertIndex)
        {
            using var dlg = new System.Windows.Forms.OpenFileDialog
            {
                Title = "Chọn file PDF để chèn trang",
                Filter = "PDF (*.pdf)|*.pdf",
                Multiselect = false
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            string fullPath;
            try { fullPath = Path.GetFullPath(dlg.FileName); }
            catch { return; }

            var existing = _groups.FirstOrDefault(g =>
                string.Equals(g.SourcePath, fullPath, StringComparison.OrdinalIgnoreCase));
            if (ReferenceEquals(existing, target))
            {
                // Chèn chính file đang xem vào chính nó → nhân bản toàn bộ trang tại vị trí chọn.
                _workspace.Execute(new MovePagesCommand(_workspace, target, target, target.Pages.ToList(), insertIndex, copy: true));
            }
            else if (existing != null)
            {
                // File đã mở thành window riêng: CHÉP trang sang (không rút trang khỏi window kia như
                // khi gộp) — user mở nó vì mục đích khác, giữ nguyên.
                _workspace.Execute(new MovePagesCommand(_workspace, existing, target, existing.Pages.ToList(), insertIndex, copy: true));
            }
            else
            {
                // Luồng mở file + ghép sẵn có: mở thành window mới rồi MergeDocumentsCommand gộp vào đúng
                // vị trí. Undo tách lại thành window riêng, giống Undo của nút Ghép.
                var opened = await AddFileAsGroup(fullPath);
                if (opened == null)
                {
                    MessageBox.Show(this, "Không mở được file:\n" + fullPath, "Chèn trang",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!_groups.Contains(target)) return;
                _workspace.Execute(new MergeDocumentsCommand(_workspace, new[] { target, opened }, null, insertIndex));
            }

            ReaderWindow.Instance?.NotifyPagesChanged(target);
            ReleaseUnusedPdfDocuments();
            _ = Dispatcher.InvokeAsync(QueueVisibleThumbnailScans, System.Windows.Threading.DispatcherPriority.ContextIdle);
            UpdateStatusBar();
        }

        // ── Xuất trang đã chọn ra file mới ─────────────────────────────────

        async Task IReaderPageEditHost.ExtractPagesAsync(DocumentGroup group, IReadOnlyList<PageRow> pages)
        {
            if (pages.Count == 0) return;

            string? initialDir = Path.GetDirectoryName(pages[0].SourcePath);
            using var dlg = new System.Windows.Forms.SaveFileDialog
            {
                Title = $"Xuất {pages.Count} trang ra file PDF mới",
                Filter = "PDF (*.pdf)|*.pdf",
                DefaultExt = "pdf",
                FileName = FileNamingMath.SuggestExtractFileName(group.FileName, pages.Select(p => group.Pages.IndexOf(p) + 1)),
                InitialDirectory = !string.IsNullOrWhiteSpace(initialDir) && Directory.Exists(initialDir) ? initialDir : ""
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            string outputPath = dlg.FileName;

            if (pages.Any(p => string.Equals(Path.GetFullPath(p.SourcePath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(this, "Không thể xuất đè lên chính file nguồn đang mở.", "Xuất trang",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var pageList = pages.Select(p => (p.SourcePath, p.PageNumber)).ToList();
            string err = "";
            // Cùng máy ghép trang của nút Lưu; chỉ gộp layer trùng TÊN (không đổi tên/collapse layer
            // như khi lưu bản ghép) — xuất trang phải giữ nguyên layer như file gốc.
            bool ok = await Task.Run(() => XTPdfMerger.TryMergePages(pageList, outputPath, out err, null,
                mergeLayersByName: true));
            if (!ok)
            {
                MessageBox.Show(this, "Xuất trang thất bại:\n" + err, "Xuất trang", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var openResult = MessageBox.Show(this,
                $"Đã xuất {pages.Count} trang ra:\n{outputPath}\n\nMở file ngay?",
                "Xuất trang", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (openResult == MessageBoxResult.Yes)
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(outputPath) { UseShellExecute = true }); }
                catch { }
            }
        }
    }

    /// <summary>Những gì Viewer cần từ cửa sổ chủ để chạy các công cụ sửa trang.</summary>
    internal interface IReaderPageEditHost
    {
        /// <summary>Trang đang chọn trong Organizer của window này, theo thứ tự trong window.</summary>
        IReadOnlyList<PageRow> GetSelectedPages(DocumentGroup group);
        Task RotatePagesAsync(IReadOnlyList<PageRow> pages, int deltaDegrees);
        Task InsertPagesFromFileAsync(DocumentGroup target, int insertIndex);
        Task ExtractPagesAsync(DocumentGroup group, IReadOnlyList<PageRow> pages);
        void DeletePages(DocumentGroup group, IReadOnlyList<PageRow> pages);
        /// <summary>Ghi các thay đổi annotation vào file nguồn và đưa vào Undo/Redo.</summary>
        Task ApplyAnnotationChangesAsync(string path, IReadOnlyList<QuickAnnotationChange> changes, string description);
    }
}
