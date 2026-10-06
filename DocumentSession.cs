using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
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

        public DocumentSession(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            AnnotationStore.PendingChanged += _ => UpdateAnnotationDirty();
        }

        /// <summary>Tab chấm cam khi file nào mà window dùng trang còn chú thích chưa lưu.</summary>
        private void UpdateAnnotationDirty()
        {
            var pending = new HashSet<string>(_groups.SelectMany(g => g.Pages).Select(p => p.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(AnnotationStore.HasPending), StringComparer.OrdinalIgnoreCase);
            foreach (var group in _groups) group.SetAnnotationsDirty(pending.Count > 0 && group.Pages.Any(p => pending.Contains(p.SourcePath)));
        }

        public PdfWorkspace Workspace { get; } = new();
        public ObservableCollection<DocumentGroup> Documents => Workspace.Documents;
        private PdfWorkspace _workspace => Workspace;
        private ObservableCollection<DocumentGroup> _groups => Workspace.Documents;

        /// <summary>Cửa sổ làm owner cho hộp thoại — luôn là cửa sổ đọc chính.</summary>
        private static Window? OwnerWindow => Application.Current?.MainWindow;
        private static bool CanModifyGroup(DocumentGroup group)
            => !File.Exists(group.SourcePath) || Controls.PdfPermissionDialog.Require(OwnerWindow, new[] { group.SourcePath }, PdfPermissionOperation.Modify);

        /// <summary>XTGrowl's legacy signature does not mark its optional owner as nullable; call its
        /// ownerless overload during startup/shutdown rather than passing a nullable window through.</summary>
        private static void ShowSuccess(string message)
        {
            var owner = OwnerWindow;
            if (owner == null) XTStyle.Controls.XTGrowl.Success(message);
            else XTStyle.Controls.XTGrowl.Success(message, owner);
        }

        /// <summary>Vùng chọn trang trong Organizer — cửa sổ ghép gắn vào khi mở, bỏ khi đóng.</summary>
        public Func<DocumentGroup, IEnumerable<PageRow>>? OrganizerSelection { get; set; }

        /// <summary>Thumbnail cần quét lại theo viewport (cửa sổ ghép nghe để nạp thumbnail của nó).</summary>
        public event Action? ThumbnailScanRequested;
        /// <summary>Số file/trạng thái mở file đổi (thanh trạng thái cửa sổ ghép).</summary>
        public event Action? StatusChanged;
        internal (long Length, DateTime Stamp)? RecoveryStamp(string path)
            => _diskStamps.TryGetValue(Path.GetFullPath(path), out var stamp) ? (stamp.Length, stamp.Utc) : null;

        internal async Task<(Dictionary<string, string> Paths, Dictionary<string, int> Counts, List<string> Skipped)> RestoreSourcesAsync(SavedSession saved)
        {
            var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var skipped = new List<string>();
            var probes = new List<DocumentGroup>();
            foreach (var source in saved.Sources)
            {
                if (!SessionRecoveryStore.SourceUnchanged(source)) { skipped.Add(source.Path); continue; }
                string path = source.Path;
                if (source.Embedded is { } bytes)
                {
                    string folder = Path.Combine(SessionRecoveryStore.Folder, "sources");
                    Directory.CreateDirectory(folder);
                    path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".pdf");
                    await File.WriteAllBytesAsync(path, bytes);
                }
                var existing = _groups.FirstOrDefault(g => string.Equals(g.SourcePath, path, StringComparison.OrdinalIgnoreCase));
                var opened = existing ?? await AddFileAsGroup(path, recordRecent: false);
                if (opened == null) { skipped.Add(source.Path); continue; }
                if (existing == null) probes.Add(opened);
                paths[source.Path] = path;
                counts[source.Path] = opened.Pages.Count;
                if (source.Changes.Count > 0 && !AnnotationStore.HasPending(path))
                {
                    var permissions = await PdfSecurityService.ReadAsync(path);
                    if (PdfPermissionPolicy.Allows(permissions, PdfPermissionOperation.Annotate))
                    {
                        var command = new AnnotationEditCommand("Recovered annotations", path, source.Changes);
                        Workspace.History.Execute(command);
                    }
                    else skipped.Add(source.Path + " (annotations restricted)");
                }
            }
            foreach (var probe in probes) _groups.Remove(probe);
            foreach (var document in saved.Documents)
            {
                var restored = SessionRecoveryStore.RestoreDocument(document, Workspace, paths, counts);
                if (restored.Pages.Count > 0) _groups.Add(restored);
            }
            NotifyStatusChanged();
            return (paths, counts, skipped);
        }
        /// <summary>Vừa Undo/Redo (cửa sổ ghép bỏ selection cũ).</summary>
        public event Action? HistoryApplied;

        private void RequestThumbnailScan() => ThumbnailScanRequested?.Invoke();
        private void NotifyStatusChanged()
        {
            if (!_dispatcher.CheckAccess()) { _ = _dispatcher.InvokeAsync(NotifyStatusChanged); return; }
            UpdateAnnotationDirty(); // trang vừa chuyển giữa các window
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

        // ── File đổi trên đĩa (người khác lưu đè…) ─────────────────────────

        private readonly Dictionary<string, (long Length, DateTime Utc)> _diskStamps = new(StringComparer.OrdinalIgnoreCase);

        private static (long Length, DateTime Utc)? StampOf(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? (info.Length, info.LastWriteTimeUtc) : null;
            }
            catch { return null; }
        }

        /// <summary>Ghi lại dấu (cỡ, giờ ghi) hiện tại của file: gọi khi mở và sau mỗi lần CHÍNH app ghi file, để không tự báo "đã đổi".</summary>
        internal void RefreshDiskStamp(string path)
        {
            var stamp = StampOf(path);
            if (stamp == null) return;
            lock (_diskStamps) _diskStamps[path] = stamp.Value;
        }

        /// <summary>Các window có file nguồn đã bị đổi trên đĩa kể từ lần mở / lần bỏ qua gần nhất (đọc đĩa — gọi ở luồng nền).</summary>
        public IReadOnlyList<(DocumentGroup Group, DateTime ChangedLocal)> FindChangedOnDisk(IReadOnlyList<DocumentGroup> groups)
        {
            var changed = new List<(DocumentGroup, DateTime)>();
            foreach (var group in groups)
            {
                if (group.IsOpening || string.IsNullOrEmpty(group.SourcePath)) continue;
                (long Length, DateTime Utc) known;
                lock (_diskStamps)
                    if (!_diskStamps.TryGetValue(group.SourcePath, out known)) continue;
                var now = StampOf(group.SourcePath);
                if (now != null && (now.Value.Length != known.Length || now.Value.Utc != known.Utc))
                    changed.Add((group, now.Value.Utc.ToLocalTime()));
            }
            return changed;
        }

        void IReaderPageEditHost.IgnoreDiskChange(DocumentGroup group) => RefreshDiskStamp(group.SourcePath);

        /// <summary>Đóng window rồi mở lại từ đĩa (bỏ mọi bản render/đệm cũ của file). Người gọi đã hỏi nếu có thay đổi chưa lưu.</summary>
        async Task IReaderPageEditHost.ReloadGroupAsync(DocumentGroup group)
        {
            string path = group.SourcePath;
            ((IReaderPageEditHost)this).CloseDocument(group);
            try { PdfFileBuffer.Invalidate(Path.GetFullPath(path), PdfFileBuffer.InvalidateReason.Changed); } catch { }
            await OpenFilesInReaderAsync(new[] { path });
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
                .Select(path => AddFileAsGroup(path))
                .ToArray();

            return Task.WhenAll(tasks);
        }

        /// <returns>Group vừa mở xong (đã có trang), hoặc null nếu file đã có window riêng / đang mở
        /// dở / không đọc được.</returns>
        internal async Task<DocumentGroup?> AddFileAsGroup(string fullPath, bool recordRecent = true)
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
                PdfOpenResult openResult;
                bool passwordRejected = false;
                while (true)
                {
                    Interlocked.Increment(ref _activeFileLoadCount);
                    try
                    {
                        openResult = await Task.Run(() => PdfThumbnailService.TryGetPageCountAsync(fullPath)).ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _activeFileLoadCount);
                    }

                    if (openResult.Failure != PdfOpenFailure.Password) break;
                    string? password = await _dispatcher.InvokeAsync(() => PromptForPdfPassword(fullPath, passwordRejected));
                    if (password == null)
                    {
                        await PdfThumbnailService.ForgetDocumentPasswordAsync(fullPath).ConfigureAwait(false);
                        await _dispatcher.InvokeAsync(() => _groups.Remove(placeholder));
                        return null;
                    }
                    passwordRejected = true;
                    await PdfThumbnailService.SetDocumentPasswordAsync(fullPath, password).ConfigureAwait(false);
                }

                await _dispatcher.InvokeAsync(() => FinishOpeningGroup(placeholder, fullPath, openResult));
                RefreshDiskStamp(fullPath);
                if (recordRecent && openResult.PageCount > 0 && !BlankPageService.IsBlankFile(fullPath)) RecentFilesStore.NoteOpened(fullPath, openResult.PageCount);
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
        private void FinishOpeningGroup(DocumentGroup group, string fullPath, PdfOpenResult openResult)
        {
            // User có thể đã tự đóng card "đang mở" (nút X) trước khi đếm trang xong.
            if (!_groups.Contains(group)) return;

            if (openResult.PageCount <= 0)
            {
                group.SetOpening(false);
                group.SetLoadError(OpenFailureText(openResult.Failure));
                return;
            }

            group.Pages.AddRange(Enumerable.Range(1, openResult.PageCount).Select(p => _workspace.CreatePlacement(fullPath, p)));
            group.SetBaseline();
            group.SetOpening(false);

            RequestThumbnailScan();
            if (group.Pages.Count > 0 && ReaderWindow.Instance is { HasAnyPageShown: false } reader)
                _ = reader.ShowFirstPageAsync(group);
            // Unvisited tabs only need metadata. The reader and organizer request their visible thumbnails.

            // Không render hết ngay ở đây. Thumbnail được đưa qua queue theo viewport thật
            // của từng ListBox PDF; prefetch nền chỉ cache ảnh và tự nhường cho vùng đang thấy.
        }

        private static string OpenFailureText(PdfOpenFailure failure) => failure switch
        {
            PdfOpenFailure.File => "Could not read the file",
            PdfOpenFailure.Format => "The file is damaged or is not a supported PDF",
            PdfOpenFailure.Password => "Password required",
            PdfOpenFailure.Security => "This PDF uses an unsupported security handler",
            _ => "Could not open the file (damaged, or its page count could not be read)"
        };

        /// <summary>Mật khẩu chỉ được gửi tới PDFium rồi giữ trong RAM của phiên cho đúng file này.</summary>
        private static string? PromptForPdfPassword(string fullPath, bool rejected)
        {
            var passwordBox = new System.Windows.Controls.PasswordBox { MinWidth = 340 };
            var message = new System.Windows.Controls.TextBlock
            {
                Text = rejected ? "That password was not accepted. Try again." : "This PDF is password-protected.",
                TextWrapping = TextWrapping.Wrap
            };
            var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = Path.GetFileName(fullPath),
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 360
            });
            panel.Children.Add(new System.Windows.Controls.TextBlock { Height = 10 });
            panel.Children.Add(message);
            panel.Children.Add(new System.Windows.Controls.TextBlock { Height = 14 });
            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Password" });
            panel.Children.Add(passwordBox);

            var buttons = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0)
            };
            var cancel = new XTStyle.Controls.XTButton { Text = "Cancel", MinWidth = 92, Height = 36, IsCancel = true };
            var open = new XTStyle.Controls.XTButton { Text = "Open", MinWidth = 92, Height = 36, IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
            open.SetResourceReference(FrameworkElement.StyleProperty, "UiPrimaryButton");
            buttons.Children.Add(cancel);
            buttons.Children.Add(open);
            panel.Children.Add(buttons);

            var dialog = new XTStyle.Controls.XTWindow
            {
                Title = "Password required",
                Content = panel,
                Width = 410,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                WindowStartupLocation = OwnerWindow == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
                Owner = OwnerWindow
            };
            dialog.TitleBarMode = XTStyle.Controls.TitleBarMode.Dialog;
            dialog.TitleIcon = Application.Current?.TryFindResource("App.Icon.Logo");
            dialog.TitleIconBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 112, 24));
            dialog.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "Ui.Surface");
            dialog.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "Ui.Text");
            dialog.UseLayoutRounding = dialog.SnapsToDevicePixels = true;
            open.Click += (_, _) => dialog.DialogResult = true;
            dialog.Loaded += (_, _) => passwordBox.Focus();
            return dialog.ShowDialog() == true ? passwordBox.Password : null;
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
            // Group đang "Opening" chưa có PageRow, nhưng source vẫn là active (đặc biệt khi user vừa nhập
            // mật khẩu). Nếu chỉ lấy Pages thì một thao tác nền có thể thu hồi lease/mật khẩu giữa lần thử mở.
            var active = new HashSet<string>(_groups
                .Where(g => !string.IsNullOrWhiteSpace(g.SourcePath))
                .Select(g => g.SourcePath), StringComparer.OrdinalIgnoreCase);
            // File không còn window nào dùng: bỏ chú thích trong bộ nhớ (chưa lưu thì người dùng đã được hỏi khi đóng tab).
            foreach (string path in _annotationFiles.Where(p => !active.Contains(p)).ToList())
            {
                AnnotationStore.Forget(path);
                AnnotationWorkingCopy.Forget(path);
                _annotationFiles.Remove(path);
            }
            foreach (string path in active) _annotationFiles.Add(path);
            ThumbnailCache.Invalidate(key => !active.Contains(key.Path));
            ReaderWindow.ReleaseUnusedSources(active);
            PdfLayerStateStore.ForgetAllExcept(active);
            PdfThumbnailService.ReleaseUnusedDocuments(active);
        }


        // ── Sửa trang / annotation / layer (gọi từ ribbon của cửa sổ đọc) ─────────

        /// <summary>Mọi lần ghi đè file nguồn đi tuần tự — Undo/Redo bấm liên tục không được ghi chồng
        /// lên nhau trên cùng 1 file.</summary>
        private static readonly SemaphoreSlim _sourceEditGate = new(1, 1);
        private readonly HashSet<string> _annotationFiles = new(StringComparer.OrdinalIgnoreCase);

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
            if (!await Controls.PdfPermissionDialog.RequireAsync(OwnerWindow, targets.Select(t => t.Path), PdfPermissionOperation.Modify)) return;

            if (!await ApplySourceRotationAsync(targets, deltaDegrees)) return;
            _workspace.History.Record(new RotateSourcePagesCommand(deltaDegrees,
                targets.Sum(t => t.Pages.Count), delta => ApplySourceRotationAsync(targets, delta), targets.Select(t => t.Path).ToList()));
        }

        private async Task<bool> ApplySourceRotationAsync(
            IReadOnlyList<(string Path, IReadOnlyCollection<int> Pages)> targets, int deltaDegrees)
        {
            bool ok = await EditSourceFilesAsync(targets, (path, pages) => PdfPageEditService.RotatePages(path, pages, deltaDegrees),
                geometryChanged: true, appendOnly: true);
            if (ok) foreach (var (path, pages) in targets) AnnotationStore.PagesRotated(path, pages, deltaDegrees);
            return ok;
        }

        // ── Annotation (Typewriter / Ghi chú / Highlight) ─────────────────

        /// <summary>Chú thích đổi trong BỘ NHỚ (vẽ lại ngay), có Undo/Redo; chỉ ghi vào file khi Save.</summary>
        Task IReaderPageEditHost.ApplyAnnotationChangesAsync(string path, IReadOnlyList<QuickAnnotationChange> changes, string description)
        {
            if (changes.Count == 0) return Task.CompletedTask;
            if (!Controls.PdfPermissionDialog.Require(OwnerWindow, new[] { path }, PdfPermissionOperation.Annotate)) return Task.CompletedTask;
            _workspace.Execute(new AnnotationEditCommand(description, path, changes.ToList()));
            NotifyStatusChanged();
            return Task.CompletedTask;
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

        /// <summary>"Export PDF with this view…": lưu bản sao các trang của window (layer cùng tên gộp) ra file mới với layer đang tắt
        /// làm mặc định. File xuất vẫn có layer (bật/tắt lại được); window đang xem không đổi.</summary>
        async Task IReaderPageEditHost.ExportWithLayerViewAsync(DocumentGroup group, IReadOnlySet<string> hiddenNames, string viewName)
        {
            var pageList = group.Pages.Select(p => (p.SourcePath, p.PageNumber)).ToList();
            if (pageList.Count == 0) return;

            string dir = Path.GetDirectoryName(group.SourcePath) ?? "";
            string suffix = string.Concat(viewName.Where(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0));
            using var dlg = new System.Windows.Forms.SaveFileDialog
            {
                Title = "Export PDF with this view",
                Filter = "PDF (*.pdf)|*.pdf",
                DefaultExt = "pdf",
                FileName = Path.GetFileNameWithoutExtension(group.SourcePath) + " - " + suffix + ".pdf",
                InitialDirectory = Directory.Exists(dir) ? dir : ""
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            string output;
            try { output = Path.GetFullPath(dlg.FileName); }
            catch { return; }
            if (_groups.Any(g => g.Pages.Any(p => string.Equals(p.SourcePath, output, StringComparison.OrdinalIgnoreCase))))
            {
                AppDialog.Show(OwnerWindow, "That file is open or used by an open window. Choose a different name.",
                    "Export PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!await Controls.SignedPdfConfirmation.ConfirmAsync(OwnerWindow,
                pageList.Select(p => p.SourcePath), "Export PDF with this layer view", false, new[] { output })) return;
            string error = "";
            bool ok = await Task.Run(() =>
            {
                try
                {
                    PdfFileTransaction.Run(new[] { output }, (_, stage) =>
                    {
                        if (!XTPdfMerger.TryMergePages(pageList, stage, out var e, null, mergeLayersByName: true)) throw new IOException(e);
                        PdfLayerService.SetDefaultVisibilityByName(stage, hiddenNames);
                    });
                    return true;
                }
                catch (Exception ex) { error = ex.Message; return false; }
            });
            if (!ok)
            {
                AppDialog.Show(OwnerWindow, "Could not export the file:\n" + error, "Export PDF", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            ShowSuccess("Exported " + Path.GetFileName(output));
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

        Task IReaderPageEditHost.OpenFilesAsync() => ((IReaderPageEditHost)this).OpenFilesDialogAsync(null);

        async Task IReaderPageEditHost.SetCommentResolvedAsync(string path, int pageNumber, string name, bool resolved)
        {
            var page = await AnnotationStore.GetPageAsync(path, pageNumber);
            if (page?.Annotations.FirstOrDefault(a => a.Name == name) is not { } spec || spec.Resolved == resolved) return;
            await ((IReaderPageEditHost)this).ApplyAnnotationChangesAsync(path,
                new[] { new QuickAnnotationChange(spec, spec with { Resolved = resolved }) }, resolved ? "Resolve comment" : "Reopen comment");
        }

        async Task IReaderPageEditHost.MergeAllToFileAsync()
        {
            var pageList = _groups.SelectMany(g => g.Pages).Select(p => (p.SourcePath, p.PageNumber)).ToList();
            if (pageList.Count == 0) return;
            string folder = AppSettings.LastMergeFolder;
            if (!Directory.Exists(folder)) folder = Path.GetDirectoryName(pageList[0].SourcePath) ?? "";
            var dialog = new Controls.MergeSaveWindow(pageList, folder) { Owner = OwnerWindow };
            if (dialog.ShowDialog() != true) return;

            string output = dialog.OutputPath;
            var options = dialog.Options;
            if (!await Controls.SignedPdfConfirmation.ConfirmAsync(OwnerWindow,
                pageList.Select(p => p.SourcePath), "Merge PDFs", false, new[] { output })) return;
            string error = "";
            Mouse.OverrideCursor = Cursors.Wait;
            bool ok;
            try
            {
                pageList = await AnnotationWorkingCopy.MapAsync(pageList);
                ok = await Task.Run(() =>
                {
                    bool r = XTPdfMerger.TryMergePages(pageList, output, out var e, null, mergeLayersByName: options.MergeLayers, options: options);
                    error = e;
                    return r;
                });
            }
            catch (Exception ex) { ok = false; error = ex.Message; }
            finally { Mouse.OverrideCursor = null; }
            if (!ok)
            {
                AppDialog.Show(OwnerWindow, "Could not merge the files:\n" + error, "Merge", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            ShowSuccess($"Merged {pageList.Count} pages into {Path.GetFileName(output)}");
        }

        Task IReaderPageEditHost.OpenPathsAsync(IEnumerable<string> paths) => OpenFilesInReaderAsync(paths);

        async Task IReaderPageEditHost.OpenFilesDialogAsync(string? initialDirectory)
        {
            using var dlg = new System.Windows.Forms.OpenFileDialog
            {
                Title = "Open PDF files",
                Filter = "PDF and XT sets (*.pdf;*.xtset)|*.pdf;*.xtset|PDF (*.pdf)|*.pdf|XT set (*.xtset)|*.xtset",
                Multiselect = true
            };
            if (!string.IsNullOrEmpty(initialDirectory) && Directory.Exists(initialDirectory)) dlg.InitialDirectory = initialDirectory;
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            await OpenFilesInReaderAsync(dlg.FileNames);
        }

        /// <summary>Mở file (hoặc chuyển sang file nếu đã mở) và hiện ngay trong cửa sổ đọc.</summary>
        internal async Task OpenFilesInReaderAsync(IEnumerable<string> paths)
        {
            DocumentGroup? last = null;
            bool lastIsNew = false;
            foreach (string path in paths)
            {
                string full;
                try { full = Path.GetFullPath(path); }
                catch { continue; }
                if (XTSetRebuild.IsSetFile(full))
                {
                    // Bộ hồ sơ XT (.xtset): mở bản ghép sẵn hoặc rebuild từ các thành phần (hỏi người dùng).
                    if (ReaderWindow.Instance is { } window) await window.OpenXtSetAsync(full);
                    continue;
                }
                var existing = _groups.FirstOrDefault(g => string.Equals(g.SourcePath, full, StringComparison.OrdinalIgnoreCase));
                if (existing != null) { last = existing; lastIsNew = false; }
                else if (await AddFileAsGroup(full) is { } added) { last = added; lastIsNew = true; }
            }
            if (last != null && last.Pages.Count > 0)
                if (ReaderWindow.Instance is { } reader)
                {
                    // File mới mở: về trang/zoom đã nhớ lần trước. File đã mở sẵn: về trang đầu như trước.
                    if (lastIsNew) await reader.ShowFirstPageAsync(last);
                    else await reader.ShowPageAsync(last, last.Pages[0], preserveZoomMode: true);
                }
        }

        void IReaderPageEditHost.CloseDocument(DocumentGroup group)
        {
            if (!_groups.Contains(group)) return;
            _workspace.Execute(new RemoveDocumentCommand(_workspace, group));
            ReaderWindow.Instance?.NotifyGroupRemoved(group);
            ReleaseUnusedPdfDocuments();
            NotifyStatusChanged();
        }

        public async void Undo()
        {
            if (!_workspace.History.CanUndo) return;
            try { await _workspace.History.UndoAsync(); }
            catch (Exception ex) { AppDialog.Show(OwnerWindow, ex.Message, "Could not undo", MessageBoxButton.OK, MessageBoxImage.Error); }
            AfterHistoryChange();
        }

        public async void Redo()
        {
            if (!_workspace.History.CanRedo) return;
            try { await _workspace.History.RedoAsync(); }
            catch (Exception ex) { AppDialog.Show(OwnerWindow, ex.Message, "Could not redo", MessageBoxButton.OK, MessageBoxImage.Error); }
            AfterHistoryChange();
        }

        // ── Xoá trang khỏi window (workspace, có Undo; Lưu mới ghi ra file) ──

        void IReaderPageEditHost.DeletePages(DocumentGroup group, IReadOnlyList<PageRow> pages)
        {
            if (!CanModifyGroup(group)) return;
            if (pages.Count == 0 || !_groups.Contains(group)) return;
            var view = ReaderWindow.Instance;
            int keepIndex = view?.CurrentPageIndex(group) ?? 0;
            var current = keepIndex < group.Pages.Count ? group.Pages[keepIndex] : null;
            int survivingBefore = group.Pages.Take(keepIndex).Count(p => !pages.Contains(p));
            _workspace.Execute(new RemovePagesCommand(_workspace, group, pages));
            if (!_groups.Contains(group)) ReaderWindow.Instance?.NotifyGroupRemoved(group);
            else if (current != null && group.Pages.Contains(current)) view?.NotifyPagesChanged(group);
            else view?.ShowPageAfterRemoval(group, survivingBefore);
            ReleaseUnusedPdfDocuments();
            NotifyStatusChanged();
        }

        /// <summary>Ghi đè từng file nguồn: đóng handle PDFium → sửa bằng iText (thread nền) → mở khoá →
        /// xoá mọi bitmap cũ của các trang đó để thumbnail/Viewer render lại ngay.</summary>
        internal async Task<bool> EditSourceFilesAsync(
            IReadOnlyList<(string Path, IReadOnlyCollection<int> Pages)> targets,
            Action<string, IReadOnlyCollection<int>> edit, bool geometryChanged, bool appendOnly = false)
        {
            if (!await Controls.PdfPermissionDialog.RequireAsync(OwnerWindow, targets.Select(t => t.Path), PdfPermissionOperation.Modify)) return false;
            if (!await Controls.SignedPdfConfirmation.ConfirmAsync(OwnerWindow,
                targets.Select(t => t.Path), "Edit PDF pages", true)) return false;
            await _sourceEditGate.WaitAsync();
            var suspensions = new List<IDisposable>();
            try
            {
                foreach (var path in targets.Select(t => t.Path).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    AnnotationStore.ReleaseReader(path);
                    suspensions.Add(await PdfThumbnailService.SuspendDocumentAsync(path, TimeSpan.FromSeconds(3)));
                }
                var byPath = targets.ToDictionary(t => Path.GetFullPath(t.Path), t => t.Pages, StringComparer.OrdinalIgnoreCase);
                if (appendOnly && byPath.Count == 1)
                {
                    // A page rotation changes only page dictionaries. Incremental saving does not
                    // replace the file, so other readers may keep their read handles open.
                    foreach (var target in byPath) await Task.Run(() => edit(target.Key, target.Value));
                }
                else await PdfFileTransaction.RunAsync(byPath.Keys.ToList(), async (path, stage) =>
                {
                    try
                    {
                        await Task.Run(() => File.Copy(path, stage));
                        if (PdfThumbnailService.TryGetDocumentPassword(path) is { Length: > 0 } password)
                            await PdfThumbnailService.SetDocumentPasswordAsync(stage, password);
                        await Task.Run(() => edit(stage, byPath[path]));
                    }
                    finally { await PdfThumbnailService.ForgetDocumentPasswordAsync(stage); }
                });
                return true;
            }
            catch (Exception ex)
            {
                AppDialog.Show(OwnerWindow, ex.Message, "Could not edit PDF pages", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            finally
            {
                foreach (var suspension in suspensions) suspension.Dispose();
                foreach (var (path, pages) in targets) InvalidateSourcePageRenders(path, pages, geometryChanged);
                _sourceEditGate.Release();
            }
        }

        /// <summary>File nguồn vừa đổi nội dung — bỏ thumbnail/ảnh Viewer/tỉ lệ trang đang giữ của đúng
        /// các trang đó (mọi placement trỏ tới chúng, ở mọi window) rồi yêu cầu render lại.
        /// <paramref name="geometryChanged"/> = false (chỉ thêm annotation): giữ ảnh Viewer cũ tới khi ảnh
        /// mới render xong để trang không chớp trắng.</summary>
        internal void InvalidateSourcePageRenders(string path, IReadOnlyCollection<int> pages, bool geometryChanged)
        {
            RefreshDiskStamp(path); // file vừa do CHÍNH app ghi
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
            if (!Controls.PdfPermissionDialog.Require(OwnerWindow, pages.Select(p => p.SourcePath), PdfPermissionOperation.Copy) || cut && !CanModifyGroup(source)) return;
            if (pages.Count == 0) return;
            _clipPages = pages.ToList();
            _clipSource = source;
            _clipCut = cut;
        }

        IReadOnlyList<PageRow> IReaderPageEditHost.PastePages(DocumentGroup target, int insertIndex)
        {
            if (!CanModifyGroup(target)) return Array.Empty<PageRow>();
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
            if (!CanModifyGroup(target) || !copy && !CanModifyGroup(source) ||
                !Controls.PdfPermissionDialog.Require(OwnerWindow, pages.Select(p => p.SourcePath), PdfPermissionOperation.Copy)) return Array.Empty<PageRow>();
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

        /// <summary>Thay từng trang cũ của <paramref name="target"/> bằng trang revision tương ứng (1 bước Undo). Trả về số trang đã thay.</summary>
        int IReaderPageEditHost.ReplaceSheets(DocumentGroup target, IReadOnlyList<(PageRow Old, DocumentGroup Source, PageRow New)> pairs)
        {
            if (pairs.Count == 0 || !_groups.Contains(target) || !CanModifyGroup(target)) return 0;
            if (!Controls.PdfPermissionDialog.Require(OwnerWindow, pairs.Select(p => p.New.SourcePath), PdfPermissionOperation.Copy)) return 0;
            var command = new ReplacePagesCommand(_workspace, pairs.Select(p => new ReplacePagesCommand.Pair(target, p.Old, p.Source, p.New)).ToList());
            _workspace.Execute(command);
            ReaderWindow.Instance?.NotifyPagesChanged(target);
            ReleaseUnusedPdfDocuments();
            RequestThumbnailScan();
            NotifyStatusChanged();
            return pairs.Count(p => !target.Pages.Contains(p.Old));
        }

        /// <summary>Chèn 1 trang trắng cùng khổ với <paramref name="reference"/> tại <paramref name="insertIndex"/>.</summary>
        async Task<IReadOnlyList<PageRow>> IReaderPageEditHost.InsertBlankPageAsync(DocumentGroup target, int insertIndex, PageRow reference)
        {
            if (!CanModifyGroup(target)) return Array.Empty<PageRow>();
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
        private static int _tempWindowCounter;

        /// <summary>Window tạm ("Temp N") không gắn file nào: gom trang từ nhiều file, lưu thì luôn "Save as".</summary>
        DocumentGroup IReaderPageEditHost.CreateTempWindow()
        {
            int number = ++_tempWindowCounter;
            var group = new DocumentGroup { SourcePath = Path.Combine(Path.GetTempPath(), $"Temp {number}.pdf") };
            group.SetDisplayName($"Temp {number}");
            group.SetBaseline();
            _groups.Add(group);
            return group;
        }

        /// <summary>Window nháp "Temp N" chứa bản sao trang của <paramref name="ordered"/> theo thứ tự; 1 bước Undo (Undo bỏ luôn window). Null nếu bị từ chối.</summary>
        DocumentGroup? IReaderPageEditHost.CreateMergeDraft(IReadOnlyList<DocumentGroup> ordered)
        {
            if (ordered.Count == 0 || ordered.Any(g => !_groups.Contains(g) || g.Pages.Count == 0)) return null;
            if (!Controls.PdfPermissionDialog.Require(OwnerWindow, ordered.SelectMany(g => g.Pages).Select(p => p.SourcePath), PdfPermissionOperation.Copy)) return null;
            int number = ++_tempWindowCounter;
            var draft = new DocumentGroup { SourcePath = Path.Combine(Path.GetTempPath(), $"Temp {number}.pdf") };
            draft.SetDisplayName($"Temp {number}");
            draft.SetBaseline();
            _workspace.Execute(new CreateMergeDraftCommand(_workspace, draft, ordered));
            ReaderWindow.Instance?.NotifyPagesChanged(draft);
            RequestThumbnailScan();
            NotifyStatusChanged();
            return draft;
        }

        private static bool IsTempWindow(DocumentGroup group) => group.IsUntitled || !File.Exists(group.SourcePath);

        /// <summary>Tạo một PDF trắng trong thư mục phiên làm việc, mở như document Untitled và chỉ chọn nơi lưu khi Save/Save As.</summary>
        internal async Task<DocumentGroup?> CreateBlankDocumentAsync(double widthPoints, double heightPoints)
        {
            string blank = await Task.Run(() => BlankPageService.CreateUntitledPdf(widthPoints, heightPoints));
            var group = await AddFileAsGroup(blank);
            if (group == null) return null;
            await _dispatcher.InvokeAsync(() => group.MarkUntitled("Untitled.pdf"));
            return group;
        }

        async Task<bool> IReaderPageEditHost.SaveGroupAsync(DocumentGroup group, bool saveAs)
        {
            if (!_groups.Contains(group) || group.Pages.Count == 0) return false;
            string target = group.SourcePath;
            if (IsTempWindow(group)) saveAs = true;
            if (!saveAs && !group.IsDirty) return true;
            if (!saveAs && !await Controls.PdfPermissionDialog.RequireAsync(OwnerWindow, new[] { target },
                    group.OnlyAnnotationsDirty ? PdfPermissionOperation.Annotate : PdfPermissionOperation.Modify)) return false;
            if (!saveAs && !group.OnlyAnnotationsDirty && (await PdfSecurityService.ReadAsync(target)).IsEncrypted)
            {
                if (AppDialog.Show(OwnerWindow, "Page structure changes require rewriting this PDF. Save as an unprotected copy instead?\n\nThe original encrypted file keeps its password and permissions.",
                    "Save protected PDF", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return false;
                saveAs = true;
            }

            if (!saveAs && _groups.Any(g => !ReferenceEquals(g, group) &&
                    g.Pages.Any(p => string.Equals(p.SourcePath, target, StringComparison.OrdinalIgnoreCase))))
            {
                var answer = AppDialog.Show(OwnerWindow,
                    "Other windows use pages from this file, so it cannot be overwritten safely.\n\nSave as a new file instead?",
                    "Save", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return false;
                saveAs = true;
            }

            if (!await Controls.SignedPdfConfirmation.ConfirmAsync(OwnerWindow,
                group.Pages.Select(p => p.SourcePath), saveAs ? "Save a PDF copy" : "Save PDF changes", !saveAs)) return false;

            // Only annotations changed and the window is exactly its own file: write them into the file (incremental update, fast).
            if (!saveAs && group.OnlyAnnotationsDirty &&
                group.Pages.All(p => string.Equals(p.SourcePath, target, StringComparison.OrdinalIgnoreCase)))
                return await SaveAnnotationsInPlaceAsync(target);

            List<(string SourcePath, int PageNumber)> pageList;
            Mouse.OverrideCursor = Cursors.Wait;
            try { pageList = await AnnotationWorkingCopy.MapAsync(group.Pages.Select(p => (p.SourcePath, p.PageNumber))); }
            catch (Exception ex) { AppDialog.Show(OwnerWindow, "Could not prepare the PDF for saving:\n" + ex.Message, "Save", MessageBoxButton.OK, MessageBoxImage.Error); return false; }
            finally { Mouse.OverrideCursor = null; }
            return saveAs ? await SaveGroupAsNewFileAsync(group, pageList) : await OverwriteSourceFileAsync(group, target, pageList);
        }

        private async Task<bool> SaveAnnotationsInPlaceAsync(string target)
        {
            var changes = AnnotationStore.Pending(target);
            if (changes.Count == 0) return true;
            await _sourceEditGate.WaitAsync();
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                AnnotationStore.ReleaseReader(target);
                using (await PdfThumbnailService.SuspendDocumentAsync(target, TimeSpan.FromSeconds(3)))
                    await Task.Run(() => AnnotationWorkingCopy.WriteInPlace(target, changes));
            }
            catch (Exception ex)
            {
                AppDialog.Show(OwnerWindow, $"Could not write the file:\n{target}\n\n{ex.Message}", "Save", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            finally
            {
                Mouse.OverrideCursor = null;
                _sourceEditGate.Release();
            }
            RefreshDiskStamp(target);
            // The page images do not change (they are rendered without annotations); the annotations are now read from the file.
            AnnotationStore.FileRewritten(target, keepPending: false);
            AnnotationWorkingCopy.Forget(target);
            _workspace.History.DiscardForSource(target);
            AfterHistoryChange();
            ReaderWindow.Instance?.ReaderSidePanel.OnSourceEdited(target);
            ShowSuccess("Saved " + Path.GetFileName(target));
            return true;
        }

        private async Task<bool> SaveGroupAsNewFileAsync(DocumentGroup group, List<(string SourcePath, int PageNumber)> pageList)
        {
            string dir = Path.GetDirectoryName(group.SourcePath) ?? "";
            using var dlg = new System.Windows.Forms.SaveFileDialog
            {
                Title = "Save as",
                Filter = "PDF (*.pdf)|*.pdf",
                DefaultExt = "pdf",
                FileName = group.IsUntitled ? "Untitled.pdf" : Path.GetFileNameWithoutExtension(group.SourcePath) + " - edited.pdf",
                InitialDirectory = Directory.Exists(dir) ? dir : ""
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return false;

            string output;
            try { output = Path.GetFullPath(dlg.FileName); }
            catch { return false; }
            if (_groups.Any(g => g.Pages.Any(p => string.Equals(p.SourcePath, output, StringComparison.OrdinalIgnoreCase))))
            {
                AppDialog.Show(OwnerWindow, "That file is open or used by an open window. Choose a different name.",
                    "Save as", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            // Source consent was obtained before preparing annotation working copies. Check an existing destination separately.
            if (!await Controls.SignedPdfConfirmation.ConfirmAsync(OwnerWindow,
                Array.Empty<string>(), "Replace an existing PDF with this copy", false, new[] { output })) return false;
            string error = "";
            bool ok = await Task.Run(() =>
            {
                bool r = XTPdfMerger.TryMergePages(pageList, output, out var e, null, mergeLayersByName: true);
                error = e;
                return r;
            });
            if (!ok)
            {
                AppDialog.Show(OwnerWindow, "Could not save the file:\n" + error, "Save as", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            var opened = await AddFileAsGroup(output);
            if (IsTempWindow(group)) ((IReaderPageEditHost)this).CloseDocument(group); // đã lưu thành file thật, cửa sổ tạm hết việc
            if (opened != null && opened.Pages.Count > 0 && ReaderWindow.Instance is { } reader)
                await reader.ShowPageAsync(opened, opened.Pages[0], preserveZoomMode: true);
            ShowSuccess("Saved " + Path.GetFileName(output));
            return true;
        }

        private async Task<bool> OverwriteSourceFileAsync(DocumentGroup group, string target, List<(string SourcePath, int PageNumber)> pageList)
        {
            int oldMax = group.Pages.Where(p => string.Equals(p.SourcePath, target, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.PageNumber).DefaultIfEmpty(0).Max();
            int keepIndex = ReaderWindow.Instance?.CurrentPageIndexIn(group) ?? 0;

            await _sourceEditGate.WaitAsync();
            try
            {
                AnnotationStore.ReleaseReader(target);
                using (await PdfThumbnailService.SuspendDocumentAsync(target, TimeSpan.FromSeconds(3)))
                    await Task.Run(() => PdfFileTransaction.Run(new[] { target }, (_, stage) =>
                    {
                        if (!XTPdfMerger.TryMergePages(pageList, stage, out string error, null, mergeLayersByName: true))
                            throw new IOException(error);
                    }));
            }
            catch (Exception ex)
            {
                AppDialog.Show(OwnerWindow, $"Could not write the file:\n{target}\n\n{ex.Message}", "Save", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            finally
            {
                _sourceEditGate.Release();
            }

            // Đặt lại window theo file vừa lưu: trang 1..N của file mới, mốc "đã lưu" mới, lịch sử cũ không còn đúng.
            int count = pageList.Count;
            _workspace.RefreshSourceDocument(target);
            var fresh = Enumerable.Range(1, count).Select(i => _workspace.CreatePlacement(target, i)).ToList();
            group.Pages.Clear();
            group.Pages.AddRange(fresh);
            AnnotationStore.FileRewritten(target, keepPending: false); // the saved file contains the edits
            AnnotationWorkingCopy.Forget(target);
            group.SetBaseline();
            UpdateAnnotationDirty();
            _workspace.History.DiscardForSource(target);
            InvalidateSourcePageRenders(target, Enumerable.Range(1, Math.Max(oldMax, count)).ToList(), geometryChanged: true);
            ReaderWindow.Instance?.OnGroupSaved(group, target, keepIndex);
            ReleaseUnusedPdfDocuments();
            AfterHistoryChange();
            ShowSuccess("Saved " + Path.GetFileName(target));
            return true;
        }

        // ── Chèn trang từ file khác ────────────────────────────────────────

        async Task IReaderPageEditHost.InsertPagesFromFileAsync(DocumentGroup target, int insertIndex)
        {
            if (!CanModifyGroup(target)) return;
            using var dlg = new System.Windows.Forms.OpenFileDialog
            {
                Title = "Choose a PDF to insert pages from",
                Filter = "PDF (*.pdf)|*.pdf",
                Multiselect = false
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            string fullPath;
            try { fullPath = Path.GetFullPath(dlg.FileName); }
            catch { return; }

            var existing = _groups.FirstOrDefault(g =>
                string.Equals(g.SourcePath, fullPath, StringComparison.OrdinalIgnoreCase));
            if (existing != null && !await Controls.PdfPermissionDialog.RequireAsync(OwnerWindow, new[] { fullPath }, PdfPermissionOperation.Copy)) return;
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
                    AppDialog.Show(OwnerWindow, "Could not open the file:\n" + fullPath, "Insert pages",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!_groups.Contains(target)) return;
                if (!await Controls.PdfPermissionDialog.RequireAsync(OwnerWindow, new[] { fullPath }, PdfPermissionOperation.Copy)) return;
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
                Title = $"Export {pages.Count} page{(pages.Count == 1 ? "" : "s")} to a new PDF",
                Filter = "PDF (*.pdf)|*.pdf",
                DefaultExt = "pdf",
                FileName = FileNamingMath.SuggestExtractFileName(group.FileName, pages.Select(p => group.Pages.IndexOf(p) + 1)),
                InitialDirectory = !string.IsNullOrWhiteSpace(initialDir) && Directory.Exists(initialDir) ? initialDir : ""
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            string outputPath = dlg.FileName;

            if (pages.Any(p => string.Equals(Path.GetFullPath(p.SourcePath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase)))
            {
                AppDialog.Show(OwnerWindow, "You cannot overwrite the source file that is currently open.", "Export pages",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!await Controls.SignedPdfConfirmation.ConfirmAsync(OwnerWindow,
                pages.Select(p => p.SourcePath), "Export selected pages", false, new[] { outputPath })) return;
            List<(string SourcePath, int PageNumber)> pageList;
            try { pageList = await AnnotationWorkingCopy.MapAsync(pages.Select(p => (p.SourcePath, p.PageNumber))); }
            catch (Exception ex) { AppDialog.Show(OwnerWindow, "Could not prepare pages for export:\n" + ex.Message, "Export pages", MessageBoxButton.OK, MessageBoxImage.Error); return; }
            string err = "";
            // Cùng máy ghép trang của nút Lưu; chỉ gộp layer trùng TÊN (không đổi tên/collapse layer
            // như khi lưu bản ghép) — xuất trang phải giữ nguyên layer như file gốc.
            bool ok = await Task.Run(() => XTPdfMerger.TryMergePages(pageList, outputPath, out err, null,
                mergeLayersByName: true));
            if (!ok)
            {
                AppDialog.Show(OwnerWindow, "Export failed:\n" + err, "Export pages", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var openResult = AppDialog.Show(OwnerWindow,
                $"Exported {pages.Count} page{(pages.Count == 1 ? "" : "s")} to:\n{outputPath}\n\nOpen the file now?",
                "Export pages", MessageBoxButton.YesNo, MessageBoxImage.Information);
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
        /// <summary>Thay trang cũ bằng trang revision (chèn bản sao ngay sau rồi bỏ trang cũ); 1 bước Undo. Trả về số trang đã thay.</summary>
        int ReplaceSheets(DocumentGroup target, IReadOnlyList<(PageRow Old, DocumentGroup Source, PageRow New)> pairs);
        /// <summary>Đặt tập layer đang tắt của file và vẽ lại thumbnail/Viewer theo trạng thái đó.</summary>
        Task SetLayerHiddenAsync(string path, IReadOnlySet<string> hidden, IReadOnlySet<string> defaultHidden);
        Task ExportWithLayerViewAsync(DocumentGroup group, IReadOnlySet<string> hiddenNames, string viewName);
        Task OpenFilesAsync();
        /// <summary>Ghép trang của MỌI window (theo thứ tự) thành 1 file PDF mới (layer cùng tên gộp).</summary>
        Task MergeAllToFileAsync();
        /// <summary>Đặt / bỏ trạng thái Resolved của 1 chú thích (ghi thẳng file nguồn).</summary>
        Task SetCommentResolvedAsync(string path, int pageNumber, string name, bool resolved);
        /// <summary>Tạo window tạm trống ("Temp N") để gom trang từ nhiều file.</summary>
        DocumentGroup CreateTempWindow();
        /// <summary>Window nháp từ bản sao trang của các window theo thứ tự (1 bước Undo).</summary>
        DocumentGroup? CreateMergeDraft(IReadOnlyList<DocumentGroup> ordered);
        /// <summary>Hộp thoại chọn file bắt đầu ở <paramref name="initialDirectory"/> (null = mặc định).</summary>
        Task OpenFilesDialogAsync(string? initialDirectory);
        Task OpenPathsAsync(IEnumerable<string> paths);
        /// <summary>Các window có file nguồn đã đổi trên đĩa (đọc đĩa — gọi ở luồng nền).</summary>
        IReadOnlyList<(DocumentGroup Group, DateTime ChangedLocal)> FindChangedOnDisk(IReadOnlyList<DocumentGroup> groups);
        /// <summary>Bỏ qua thay đổi trên đĩa của file này (ghi lại dấu hiện tại).</summary>
        void IgnoreDiskChange(DocumentGroup group);
        /// <summary>Đóng rồi mở lại window từ đĩa.</summary>
        Task ReloadGroupAsync(DocumentGroup group);
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
