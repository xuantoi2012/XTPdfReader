using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using XTPdfMergeApp.Services;
using static XTPdfMergeApp.Services.ThumbnailCache;
using XTPdfMergeApp.Workspace;
using PageRow = XTPdfMergeApp.Domain.PagePlacement;
using DocumentGroup = XTPdfMergeApp.Domain.WorkspaceDocument;

namespace XTPdfMergeApp
{
    /// <summary>
    /// Phiên làm việc của app: các file đang mở (PdfWorkspace + undo/redo), mở file, và mọi thao tác sửa
    /// trang/annotation/layer gọi từ ribbon. Do <see cref="ReaderWindow"/> (cửa sổ chính) tạo và sở hữu;
    /// cửa sổ ghép (<see cref="MergeWorkspaceWindow"/>) chỉ mượn session này khi được mở ra.
    /// </summary>
    internal sealed class DocumentSession : IReaderPageEditHost
    {
        private readonly Dispatcher _dispatcher;

        public DocumentSession(Dispatcher dispatcher) => _dispatcher = dispatcher;

        public PdfWorkspace Workspace { get; } = new();
        public ObservableCollection<DocumentGroup> Documents => Workspace.Documents;
        private PdfWorkspace _workspace => Workspace;
        private ObservableCollection<DocumentGroup> _groups => Workspace.Documents;

        /// <summary>Cửa sổ làm owner cho hộp thoại — luôn là cửa sổ đọc chính.</summary>
        private static Window? OwnerWindow => Application.Current?.MainWindow;

        /// <summary>Vùng chọn trang trong Organizer — cửa sổ ghép gắn vào khi mở, bỏ khi đóng.</summary>
        public Func<DocumentGroup, IEnumerable<PageRow>>? OrganizerSelection { get; set; }

        /// <summary>Thumbnail cần quét lại theo viewport (cửa sổ ghép nghe để nạp thumbnail của nó).</summary>
        public event Action? ThumbnailScanRequested;
        /// <summary>Số file/trạng thái mở file đổi (thanh trạng thái cửa sổ ghép).</summary>
        public event Action? StatusChanged;
        /// <summary>Vừa Undo/Redo (cửa sổ ghép bỏ selection cũ).</summary>
        public event Action? HistoryApplied;

        private void RequestThumbnailScan() => ThumbnailScanRequested?.Invoke();
        private void NotifyStatusChanged()
        {
            if (!_dispatcher.CheckAccess()) { _ = _dispatcher.InvokeAsync(NotifyStatusChanged); return; }
            StatusChanged?.Invoke();
        }

        /// <summary>Sau mọi Undo/Redo (từ bất kỳ cửa sổ nào).</summary>
        public void AfterHistoryChange()
        {
            ReleaseUnusedPdfDocuments();
            HistoryApplied?.Invoke();
            RequestThumbnailScan();
            NotifyStatusChanged();
        }

        // ── Mở file ─────────────────────────────────────────────────────

        private readonly HashSet<string> _loadingSourcePaths = new(StringComparer.OrdinalIgnoreCase);
        private int _activeFileLoadCount;
        private int _activeWarmThumbnailGroups;
        private long _warmThumbnailCompleted;
        public int ActiveFileLoadCount => Volatile.Read(ref _activeFileLoadCount);
        public int ActiveWarmThumbnailGroups => Volatile.Read(ref _activeWarmThumbnailGroups);
        public long WarmThumbnailCompleted => Interlocked.Read(ref _warmThumbnailCompleted);

        internal int LoadingFileCount
        {
            get
            {
                lock (_loadingSourcePaths)
                    return _loadingSourcePaths.Count;
            }
        }

        /// <summary>Thêm 1 FILE = 1 WINDOW MỚI (cuối danh sách) — tách sẵn từng trang. Nếu file đó ĐÃ có window riêng rồi thì bỏ qua (không tạo trùng).</summary>
        internal Task AddFilesAsGroups(IEnumerable<string> paths)
        {
            var tasks = paths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(AddFileAsGroup)
                .ToArray();

            return Task.WhenAll(tasks);
        }

        /// <returns>Group vừa mở xong (đã có trang), hoặc null nếu file đã có window riêng / đang mở
        /// dở / không đọc được.</returns>
        internal async Task<DocumentGroup?> AddFileAsGroup(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return null;

            try { fullPath = Path.GetFullPath(fullPath); }
            catch { return null; }

            if (_dispatcher.CheckAccess())
            {
                if (HasGroupForSource(fullPath)) return null;
            }
            else if (await _dispatcher.InvokeAsync(() => HasGroupForSource(fullPath)))
            {
                return null;
            }

            if (!TryReserveLoadingSource(fullPath)) return null;

            // Card hiện NGAY ở trạng thái "đang mở" trước khi biết số trang — user không phải
            // chờ round-trip đếm trang mới thấy có gì đó xuất hiện, dù việc đếm/tải vẫn cần
            // thời gian như cũ (chỉ khác lúc NÀO user thấy phản hồi, không phải làm nhanh hơn).
            DocumentGroup? placeholder = await _dispatcher.InvokeAsync(() => AddOpeningPlaceholder(fullPath));
            NotifyStatusChanged();
            if (placeholder == null)
            {
                ReleaseLoadingSource(fullPath);
                return null;
            }

            try
            {
                Interlocked.Increment(ref _activeFileLoadCount);
                int pageCount;
                try
                {
                    pageCount = await Task.Run(() => PdfThumbnailService.GetPageCountAsync(fullPath)).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeFileLoadCount);
                }

                await _dispatcher.InvokeAsync(() => FinishOpeningGroup(placeholder, fullPath, pageCount));
                return await _dispatcher.InvokeAsync(() =>
                    _groups.Contains(placeholder) && placeholder.Pages.Count > 0 ? placeholder : null);
            }
            finally
            {
                ReleaseLoadingSource(fullPath);
                NotifyStatusChanged();
            }
        }

        private bool HasGroupForSource(string fullPath)
            => _groups.Any(g => string.Equals(g.SourcePath, fullPath, StringComparison.OrdinalIgnoreCase));

        private bool TryReserveLoadingSource(string fullPath)
        {
            lock (_loadingSourcePaths)
            {
                if (_loadingSourcePaths.Contains(fullPath)) return false;
                _loadingSourcePaths.Add(fullPath);
                return true;
            }
        }

        private void ReleaseLoadingSource(string fullPath)
        {
            lock (_loadingSourcePaths)
                _loadingSourcePaths.Remove(fullPath);
        }

        private DocumentGroup? AddOpeningPlaceholder(string fullPath)
        {
            if (HasGroupForSource(fullPath)) return null;
            var group = new DocumentGroup { SourcePath = fullPath };
            group.SetOpening(true);
            _groups.Add(group);
            return group;
        }

        /// <summary>Đổ trang thật vào đúng placeholder đã hiện sẵn từ <see cref="AddOpeningPlaceholder"/>
        /// (xem AddFileAsGroup) — không tạo group mới ở đây, tránh có 2 card cho cùng 1 file.</summary>
        private void FinishOpeningGroup(DocumentGroup group, string fullPath, int pageCount)
        {
            // User có thể đã tự đóng card "đang mở" (nút X) trước khi đếm trang xong.
            if (!_groups.Contains(group)) return;

            if (pageCount <= 0)
            {
                group.SetOpening(false);
                group.SetLoadError("Không mở được file (file hỏng hoặc không đọc được số trang)");
                return;
            }

            group.Pages.AddRange(Enumerable.Range(1, pageCount).Select(p => _workspace.CreatePlacement(fullPath, p)));
            group.SetBaseline();
            group.SetOpening(false);

            RequestThumbnailScan();
            if (group.Pages.Count > 0 && ReaderWindow.Instance is { HasAnyPageShown: false } reader)
                _ = reader.ShowPageAsync(group, group.Pages[0], preserveZoomMode: false);
            _ = WarmInitialThumbnailsAsync(group);

            // Không render hết ngay ở đây. Thumbnail được đưa qua queue theo viewport thật
            // của từng ListBox PDF; prefetch nền chỉ cache ảnh và tự nhường cho vùng đang thấy.
        }

        private async Task WarmInitialThumbnailsAsync(DocumentGroup group)
        {
            Interlocked.Increment(ref _activeWarmThumbnailGroups);
            try
            {
                List<PageRow> pages = await _dispatcher.InvokeAsync(() =>
                    _groups.Contains(group) ? group.Pages.Take(InitialWarmThumbnailCount).ToList() : new List<PageRow>());

                foreach (var row in pages)
                {
                    var key = ThumbnailKey(row);
                    BitmapSource? bmp;

                    try { bmp = await AwaitThumbnailLoadAsync(key, foreground: true).ConfigureAwait(false); }
                    catch { continue; }

                    if (bmp == null) continue;
                    await _dispatcher.InvokeAsync(() =>
                    {
                        if (_groups.Contains(group) && group.Pages.Contains(row) && row.Thumbnail == null &&
                            string.Equals(PdfLayerStateStore.GetToken(row.SourcePath), key.Layers, StringComparison.Ordinal))
                        {
                            row.Thumbnail = bmp;
                            Interlocked.Increment(ref _warmThumbnailCompleted);
                        }
                    }, DispatcherPriority.Send);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _activeWarmThumbnailGroups);
            }
        }

        internal void ReleaseUnusedPdfDocuments()
        {
            var active = new HashSet<string>(_groups.SelectMany(g => g.Pages.Select(p => p.SourcePath)), StringComparer.OrdinalIgnoreCase);
            ThumbnailCache.Invalidate(key => !active.Contains(key.Path));
            ReaderWindow.ReleaseUnusedSources(active);
            PdfLayerStateStore.ForgetAllExcept(active);
            PdfThumbnailService.ReleaseUnusedDocuments(active);
        }


        // ── Sửa trang / annotation / layer (gọi từ ribbon của cửa sổ đọc) ─────────

        /// <summary>Mọi lần ghi đè file nguồn đi tuần tự — Undo/Redo bấm liên tục không được ghi chồng
        /// lên nhau trên cùng 1 file.</summary>
        private static readonly SemaphoreSlim _sourceEditGate = new(1, 1);

        IReadOnlyList<PageRow> IReaderPageEditHost.GetSelectedPages(DocumentGroup group)
        {
            // Chỉ có khi cửa sổ ghép đang mở (nó cung cấp vùng chọn trong Organizer).
            var selected = OrganizerSelection?.Invoke(group);
            if (selected == null) return Array.Empty<PageRow>();
            return selected
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

        // ── Layer (OCG): bật/tắt theo Cách 2 trong báo cáo điều tra ─────────

        /// <summary>Đổi tập layer đang tắt của 1 file: lưu trạng thái (PdfLayerStateStore) → đóng lease PDFium
        /// hiện tại của file (lần vẽ sau PdfThumbnailService.LoadDocumentLease tự mở lại bằng
        /// FPDF_LoadCustomDocument theo trạng thái mới) → cho thumbnail và Viewer lấy ảnh theo key mới.
        /// Không xoá cache: ảnh của các trạng thái đã từng vẽ vẫn còn, bật/tắt qua lại trúng cache ngay.</summary>
        async Task IReaderPageEditHost.SetLayerHiddenAsync(string path, IReadOnlySet<string> hidden, IReadOnlySet<string> defaultHidden)
        {
            if (!PdfLayerStateStore.SetHidden(path, hidden, defaultHidden)) return;
            await _sourceEditGate.WaitAsync();
            try { await PdfThumbnailService.RetireDocumentAsync(path); }
            finally { _sourceEditGate.Release(); }
            RefreshRendersAfterLayerChange(path);
        }

        private void RefreshRendersAfterLayerChange(string path)
        {
            var rows = _groups.SelectMany(g => g.Pages)
                .Where(p => string.Equals(p.SourcePath, path, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var hadThumbnail = new List<PageRow>();
            foreach (var row in rows)
            {
                // Không để ảnh trạng thái cũ hiện lẫn: bỏ trước, nạp lại ngay theo key mới (trúng cache nếu
                // trạng thái này đã từng vẽ).
                if (row.Thumbnail != null) hadThumbnail.Add(row);
                row.Thumbnail = null;
                row.ThumbnailLoadQueued = false;
            }

            ReaderWindow.Instance?.OnLayerStateChanged(path);
            foreach (var row in hadThumbnail) _ = LoadThumbnailFor(row);
            RequestThumbnailScan();
        }

        // ── Lệnh cửa sổ đọc gọi sang (mở/đóng file, undo, cửa sổ ghép) ─────────

        async Task IReaderPageEditHost.OpenFilesAsync()
        {
            using var dlg = new System.Windows.Forms.OpenFileDialog
            {
                Title = "Mở file PDF",
                Filter = "PDF (*.pdf)|*.pdf",
                Multiselect = true
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            await OpenFilesInReaderAsync(dlg.FileNames);
        }

        /// <summary>Mở file (hoặc chuyển sang file nếu đã mở) và hiện ngay trong cửa sổ đọc.</summary>
        internal async Task OpenFilesInReaderAsync(IEnumerable<string> paths)
        {
            DocumentGroup? last = null;
            foreach (string path in paths)
            {
                string full;
                try { full = Path.GetFullPath(path); }
                catch { continue; }
                last = _groups.FirstOrDefault(g => string.Equals(g.SourcePath, full, StringComparison.OrdinalIgnoreCase))
                       ?? await AddFileAsGroup(full) ?? last;
            }
            if (last != null && last.Pages.Count > 0)
                if (ReaderWindow.Instance is { } reader) await reader.ShowPageAsync(last, last.Pages[0], preserveZoomMode: true);
        }

        void IReaderPageEditHost.CloseDocument(DocumentGroup group)
        {
            if (!_groups.Contains(group)) return;
            _workspace.Execute(new RemoveDocumentCommand(_workspace, group));
            ReaderWindow.Instance?.NotifyGroupRemoved(group);
            ReleaseUnusedPdfDocuments();
            NotifyStatusChanged();
        }

        public void Undo()
        {
            if (!_workspace.History.CanUndo) return;
            _workspace.History.Undo();
            AfterHistoryChange();
        }

        public void Redo()
        {
            if (!_workspace.History.CanRedo) return;
            _workspace.History.Redo();
            AfterHistoryChange();
        }

        // ── Xoá trang khỏi window (workspace, có Undo; Lưu mới ghi ra file) ──

        void IReaderPageEditHost.DeletePages(DocumentGroup group, IReadOnlyList<PageRow> pages)
        {
            if (pages.Count == 0 || !_groups.Contains(group)) return;
            _workspace.Execute(new RemovePagesCommand(_workspace, group, pages));
            if (!_groups.Contains(group)) ReaderWindow.Instance?.NotifyGroupRemoved(group);
            else ReaderWindow.Instance?.NotifyPagesChanged(group);
            ReleaseUnusedPdfDocuments();
            NotifyStatusChanged();
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
                        MessageBox.Show(OwnerWindow, $"Không ghi được file:\n{path}\n\n{ex.Message}", "Sửa PDF",
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

            ThumbnailCache.Invalidate(key => Matches(key.Path, key.Page));

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
                // Xoay trang đổi khổ (rộng ↔ cao): Viewer đọc lại kích thước thật (EnsureContinuousPageSizesAsync).
                row.PageWidthPoints = null;
                row.PageHeightPoints = null;
            }

            ReaderWindow.Instance?.OnSourcePagesEdited(path, pages, geometryChanged);
            foreach (var row in affected) _ = LoadThumbnailFor(row);
            RequestThumbnailScan();
        }

        // ── Clipboard trang, Move, Duplicate (panel Pages) ─────────────────

        private List<PageRow>? _clipPages;
        private DocumentGroup? _clipSource;
        private bool _clipCut;

        bool IReaderPageEditHost.HasPageClipboard => _clipPages is { Count: > 0 };

        /// <summary>Copy/Cut: chỉ ghi nhớ. Cut không xoá trang cho tới khi Paste (lúc đó trang được di chuyển, có Undo).</summary>
        void IReaderPageEditHost.CopyPages(DocumentGroup source, IReadOnlyList<PageRow> pages, bool cut)
        {
            if (pages.Count == 0) return;
            _clipPages = pages.ToList();
            _clipSource = source;
            _clipCut = cut;
        }

        IReadOnlyList<PageRow> IReaderPageEditHost.PastePages(DocumentGroup target, int insertIndex)
        {
            if (_clipPages is not { Count: > 0 } || !_groups.Contains(target)) return Array.Empty<PageRow>();
            var pages = _clipPages.ToList();
            bool move = _clipCut && _clipSource != null && _groups.Contains(_clipSource)
                        && pages.All(p => _clipSource.Pages.Contains(p));
            var source = move ? _clipSource! : (_clipSource != null && _groups.Contains(_clipSource) ? _clipSource : target);
            var inserted = ((IReaderPageEditHost)this).MovePages(source, target, pages, insertIndex, copy: !move);
            if (move) { _clipPages = null; _clipSource = null; _clipCut = false; }
            return inserted;
        }

        IReadOnlyList<PageRow> IReaderPageEditHost.MovePages(DocumentGroup source, DocumentGroup target,
            IReadOnlyList<PageRow> pages, int insertIndex, bool copy)
        {
            if (pages.Count == 0 || !_groups.Contains(source) || !_groups.Contains(target)) return Array.Empty<PageRow>();
            var command = new MovePagesCommand(_workspace, source, target, pages, insertIndex, copy);
            _workspace.Execute(command);
            foreach (var group in new[] { source, target }.Distinct())
            {
                if (!_groups.Contains(group)) ReaderWindow.Instance?.NotifyGroupRemoved(group);
                else ReaderWindow.Instance?.NotifyPagesChanged(group);
            }
            ReleaseUnusedPdfDocuments();
            RequestThumbnailScan();
            NotifyStatusChanged();
            return command.InsertedPages;
        }

        /// <summary>Chèn 1 trang trắng cùng khổ với <paramref name="reference"/> tại <paramref name="insertIndex"/>.</summary>
        async Task<IReadOnlyList<PageRow>> IReaderPageEditHost.InsertBlankPageAsync(DocumentGroup target, int insertIndex, PageRow reference)
        {
            if (!_groups.Contains(target)) return Array.Empty<PageRow>();
            double width = reference.PageWidthPoints ?? 0, height = reference.PageHeightPoints ?? 0;
            string blank = await Task.Run(() =>
            {
                if (width <= 0 || height <= 0)
                {
                    var size = BlankPageService.ReadPageSize(reference.SourcePath, reference.PageNumber);
                    (width, height) = size ?? (595.276, 841.89); // A4 nếu không đọc được khổ
                }
                return BlankPageService.GetBlankPdf(width, height);
            });
            if (!_groups.Contains(target)) return Array.Empty<PageRow>();
            var command = new InsertPagesCommand(target, new[] { _workspace.CreatePlacement(blank, 1) }, insertIndex, "Insert blank page");
            _workspace.Execute(command);
            ReaderWindow.Instance?.NotifyPagesChanged(target);
            RequestThumbnailScan();
            NotifyStatusChanged();
            return command.Inserted;
        }

        // ── Lưu (Save / Save As) ───────────────────────────────────────────

        /// <summary>Window có thay đổi chưa lưu (theo thứ tự tab).</summary>
        IReadOnlyList<DocumentGroup> IReaderPageEditHost.GetDirtyGroups() => _groups.Where(g => g.IsDirty).ToList();

        /// <summary>Lưu danh sách trang hiện tại của window. Save ghi đè file gốc (viết file tạm cùng thư mục rồi thay thế; bản sao lưu
        /// tạm bị xoá khi thành công). Save As ghi file mới và mở nó thành tab mới. Nếu window khác đang dùng trang của file này thì
        /// không ghi đè (số trang sẽ lệch) — chuyển sang Save As.</summary>
        async Task<bool> IReaderPageEditHost.SaveGroupAsync(DocumentGroup group, bool saveAs)
        {
            if (!_groups.Contains(group) || group.Pages.Count == 0) return false;
            string target = group.SourcePath;
            if (!saveAs && !group.IsDirty) return true;

            if (!saveAs && _groups.Any(g => !ReferenceEquals(g, group) &&
                    g.Pages.Any(p => string.Equals(p.SourcePath, target, StringComparison.OrdinalIgnoreCase))))
            {
                var answer = MessageBox.Show(OwnerWindow,
                    "Other windows use pages from this file, so it cannot be overwritten safely.\n\nSave as a new file instead?",
                    "Save", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return false;
                saveAs = true;
            }

            var pageList = group.Pages.Select(p => (p.SourcePath, p.PageNumber)).ToList();
            return saveAs ? await SaveGroupAsNewFileAsync(group, pageList) : await OverwriteSourceFileAsync(group, target, pageList);
        }

        private async Task<bool> SaveGroupAsNewFileAsync(DocumentGroup group, List<(string SourcePath, int PageNumber)> pageList)
        {
            string dir = Path.GetDirectoryName(group.SourcePath) ?? "";
            using var dlg = new System.Windows.Forms.SaveFileDialog
            {
                Title = "Save as",
                Filter = "PDF (*.pdf)|*.pdf",
                DefaultExt = "pdf",
                FileName = Path.GetFileNameWithoutExtension(group.SourcePath) + " - edited.pdf",
                InitialDirectory = Directory.Exists(dir) ? dir : ""
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return false;

            string output;
            try { output = Path.GetFullPath(dlg.FileName); }
            catch { return false; }
            if (_groups.Any(g => g.Pages.Any(p => string.Equals(p.SourcePath, output, StringComparison.OrdinalIgnoreCase))))
            {
                MessageBox.Show(OwnerWindow, "That file is open or used by an open window. Choose a different name.",
                    "Save as", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            string error = "";
            bool ok = await Task.Run(() =>
            {
                bool r = XTPdfMerger.TryMergePages(pageList, output, out var e, null, mergeLayersByName: true);
                error = e;
                return r;
            });
            if (!ok)
            {
                MessageBox.Show(OwnerWindow, "Could not save the file:\n" + error, "Save as", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            var opened = await AddFileAsGroup(output);
            if (opened != null && opened.Pages.Count > 0 && ReaderWindow.Instance is { } reader)
                await reader.ShowPageAsync(opened, opened.Pages[0], preserveZoomMode: true);
            XTStyle.Controls.XTGrowl.Success("Saved " + Path.GetFileName(output), OwnerWindow);
            return true;
        }

        private async Task<bool> OverwriteSourceFileAsync(DocumentGroup group, string target, List<(string SourcePath, int PageNumber)> pageList)
        {
            string temp = target + ".xtsave.tmp", backup = target + ".xtsave.bak";
            string error = "";
            bool ok = await Task.Run(() =>
            {
                bool r = XTPdfMerger.TryMergePages(pageList, temp, out var e, null, mergeLayersByName: true);
                error = e;
                return r;
            });
            if (!ok)
            {
                TryDelete(temp);
                MessageBox.Show(OwnerWindow, "Could not save the file:\n" + error, "Save", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            int oldMax = group.Pages.Where(p => string.Equals(p.SourcePath, target, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.PageNumber).DefaultIfEmpty(0).Max();
            int keepIndex = ReaderWindow.Instance?.CurrentPageIndexIn(group) ?? 0;

            await _sourceEditGate.WaitAsync();
            try
            {
                using (await PdfThumbnailService.SuspendDocumentAsync(target, TimeSpan.FromSeconds(3)))
                    await Task.Run(() => File.Replace(temp, target, backup, ignoreMetadataErrors: true));
            }
            catch (Exception ex)
            {
                TryDelete(temp);
                MessageBox.Show(OwnerWindow, $"Could not write the file:\n{target}\n\n{ex.Message}", "Save", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            finally
            {
                _sourceEditGate.Release();
            }
            TryDelete(backup);

            // Đặt lại window theo file vừa lưu: trang 1..N của file mới, mốc "đã lưu" mới, lịch sử cũ không còn đúng.
            int count = pageList.Count;
            _workspace.RefreshSourceDocument(target);
            var fresh = Enumerable.Range(1, count).Select(i => _workspace.CreatePlacement(target, i)).ToList();
            group.Pages.Clear();
            group.Pages.AddRange(fresh);
            group.SetBaseline();
            _workspace.History.Clear();
            InvalidateSourcePageRenders(target, Enumerable.Range(1, Math.Max(oldMax, count)).ToList(), geometryChanged: true);
            ReaderWindow.Instance?.OnGroupSaved(group, target, keepIndex);
            ReleaseUnusedPdfDocuments();
            AfterHistoryChange();
            XTStyle.Controls.XTGrowl.Success("Saved " + Path.GetFileName(target), OwnerWindow);
            return true;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* file tạm/sao lưu: bỏ qua */ }
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
                    MessageBox.Show(OwnerWindow, "Không mở được file:\n" + fullPath, "Chèn trang",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!_groups.Contains(target)) return;
                _workspace.Execute(new MergeDocumentsCommand(_workspace, new[] { target, opened }, null, insertIndex));
            }

            ReaderWindow.Instance?.NotifyPagesChanged(target);
            ReleaseUnusedPdfDocuments();
            RequestThumbnailScan();
            NotifyStatusChanged();
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
                MessageBox.Show(OwnerWindow, "Không thể xuất đè lên chính file nguồn đang mở.", "Xuất trang",
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
                MessageBox.Show(OwnerWindow, "Xuất trang thất bại:\n" + err, "Xuất trang", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var openResult = MessageBox.Show(OwnerWindow,
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
        /// <summary>Đặt tập layer đang tắt của file và vẽ lại thumbnail/Viewer theo trạng thái đó.</summary>
        Task SetLayerHiddenAsync(string path, IReadOnlySet<string> hidden, IReadOnlySet<string> defaultHidden);
        Task OpenFilesAsync();
        void CloseDocument(DocumentGroup group);
        Task<IReadOnlyList<PageRow>> InsertBlankPageAsync(DocumentGroup target, int insertIndex, PageRow reference);
        /// <summary>Có trang trong clipboard (đã Copy/Cut) để dán.</summary>
        bool HasPageClipboard { get; }
        void CopyPages(DocumentGroup source, IReadOnlyList<PageRow> pages, bool cut);
        /// <summary>Dán vào <paramref name="target"/> trước vị trí <paramref name="insertIndex"/> (0-based); trả về các trang vừa chèn.</summary>
        IReadOnlyList<PageRow> PastePages(DocumentGroup target, int insertIndex);
        /// <summary>Di chuyển (hoặc sao chép nếu <paramref name="copy"/>) trang sang vị trí <paramref name="insertIndex"/> của window đích;
        /// trả về các placement vừa chèn. Có Undo.</summary>
        IReadOnlyList<PageRow> MovePages(DocumentGroup source, DocumentGroup target, IReadOnlyList<PageRow> pages, int insertIndex, bool copy);
        /// <summary>Lưu window: <paramref name="saveAs"/> false = ghi đè file gốc, true = file mới. Trả về true nếu đã lưu (hoặc không có gì để lưu).</summary>
        Task<bool> SaveGroupAsync(DocumentGroup group, bool saveAs);
        /// <summary>Window có thay đổi chưa lưu.</summary>
        IReadOnlyList<DocumentGroup> GetDirtyGroups();
        void Undo();
        void Redo();
        /// <summary>Ghi các thay đổi annotation vào file nguồn và đưa vào Undo/Redo.</summary>
        Task ApplyAnnotationChangesAsync(string path, IReadOnlyList<QuickAnnotationChange> changes, string description);
    }
}
