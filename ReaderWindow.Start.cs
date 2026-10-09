using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using XTStyle.Controls;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Services;
using DocumentGroup = XTPdfMergeApp.Domain.WorkspaceDocument;

namespace XTPdfMergeApp
{
    /// <summary>
    /// Màn hình Start (Recent, Quick tools), thanh vàng "file đổi trên đĩa" và bảng lệnh Ctrl+K của cửa sổ đọc
    /// (docs/UI_REDESIGN.md, P5).
    /// </summary>
    public partial class ReaderWindow
    {
        private readonly Dictionary<DocumentGroup, DateTime> _changedOnDisk = new();
        private bool _checkingDisk;

        private void InitializeStart()
        {
            StartPage.OpenDialogRequested += dir =>
            {
                ShowStart(false);
                if (EditHost != null) _ = EditHost.OpenFilesDialogAsync(dir);
            };
            StartPage.OpenPathRequested += path =>
            {
                ShowStart(false);
                if (EditHost != null) _ = EditHost.OpenPathsAsync(new[] { path });
            };
            StartPage.OpenFolderRequested += OpenFolder;
            StartPage.NewPdfRequested += () => ReaderNew_Click(this, new RoutedEventArgs());
            StartPage.MergePdfRequested += () => ReaderShowMergeWindow_Click(this, new RoutedEventArgs());
            StartPage.ExportPdfRequested += () => OpenExport(preferFlatten: false);
            StartPage.PrintPdfRequested += () => ReaderPrint_Click(this, new RoutedEventArgs());
            StartPage.CaptureRequested += () => CaptureLauncher.Run("capture", this);
            StartPage.CapturesRequested += () => CaptureLauncher.Run("store", this);
            StartPage.HasActiveDocument = () => _readerGroup?.Pages.Count > 0;
            Loaded += (_, _) => { if (_groups.Count == 0) ShowStart(true); };
            _groups.CollectionChanged += (_, _) =>
            {
                if (_groups.Count == 0) ShowStart(true);
                else if (_groups.Count > 0 && StartPage.Visibility == Visibility.Visible && _autoStart) ShowStart(false);
            };

            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(20) };
            timer.Tick += (_, _) => CheckDiskChanges();
            timer.Start();
            Activated += (_, _) => CheckDiskChanges();
        }

        // ── Start ─────────────────────────────────────────────────────

        private bool _autoStart;

        private XTButton StartButtonElement => ReaderStartButton;

        private void OpenFolder()
        {
            if (EditHost == null) return;
            using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Open every PDF in this folder", UseDescriptionForTitle = true };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            var files = Directory.GetFiles(dialog.SelectedPath, "*.pdf").OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (files.Count == 0) { AppDialog.Show(this, "There are no PDF files in that folder.", "Open folder", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            if (files.Count > 12 && AppDialog.Show(this, $"Open all {files.Count} PDF files in this folder?", "Open folder", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            ShowStart(false);
            _ = EditHost.OpenPathsAsync(files);
        }

        private void ShowStart(bool show)
        {
            if (show == (StartPage.Visibility == Visibility.Visible)) return;
            _autoStart = show && _groups.Count == 0;
            if (show)
            {
                ShowSettings(false);
                StartPage.Reload();
                Loc.ApplyTree(StartPage);
            }
            StartPage.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            // Start covers the viewer without changing its WPF visibility.
            ReaderContinuousView.IsRenderingSuspended = show;
            if (show) StopNearbyThumbnailWarmup();
            else if (_readerGroup != null) ScheduleNearbyThumbnailWarmup();
            StartButtonElement.Tag = show ? "Active" : null;
            // Only one tab lit at a time: the ListBox keeps its own selection highlight independent of Start's, so it has
            // to be cleared/restored by hand — a real TabControl wouldn't need this, but the file tabs predate this button.
            _syncingDocumentTabs = true;
            if (!show) UpdateDocumentTabs(_readerGroup);
            ReaderDocumentTabs.SelectedItem = show ? null : _readerGroup;
            _syncingDocumentTabs = false;
            UpdateToolbarVisibility();
        }

        /// <summary>Start and Merge have their own header (mockups 13 and 6), so the reader toolbar is hidden there.</summary>
        private void UpdateToolbarVisibility()
        {
            bool start = StartPage.Visibility == Visibility.Visible;
            ReaderToolbarBar.Visibility = start ? Visibility.Collapsed : Visibility.Visible;
            ReaderStatusBar.Visibility = start ? Visibility.Collapsed : Visibility.Visible;
        }

        private void StartButton_Click(object sender, RoutedEventArgs e) => ShowStart(StartPage.Visibility != Visibility.Visible);

        // ── Thanh "file đổi trên đĩa" ─────────────────────────────────

        private async void CheckDiskChanges()
        {
            if (_checkingDisk || EditHost == null || _groups.Count == 0) return;
            _checkingDisk = true;
            try
            {
                var snapshot = _groups.ToList();
                var host = EditHost;
                var changed = await Task.Run(() => host.FindChangedOnDisk(snapshot));
                _changedOnDisk.Clear();
                foreach (var (group, time) in changed)
                    if (_groups.Contains(group)) _changedOnDisk[group] = time;
                UpdateDiskBanner();
            }
            catch { /* đĩa mạng ngắt: bỏ lượt kiểm tra này */ }
            finally { _checkingDisk = false; }
        }

        private void UpdateDiskBanner()
        {
            if (_readerGroup != null && _changedOnDisk.TryGetValue(_readerGroup, out var time))
            {
                DiskChangedText.Text = Loc.T($"{_readerGroup.FileName} was changed on disk ({time:HH:mm}). What you see may be out of date.");
                DiskChangedBanner.Visibility = Visibility.Visible;
                _ = AddLastSaverToBannerAsync(_readerGroup);
            }
            else DiskChangedBanner.Visibility = Visibility.Collapsed;
        }

        private async void BannerReload_Click(object sender, RoutedEventArgs e)
        {
            var group = _readerGroup;
            if (group == null || EditHost == null) return;
            if (group.IsDirty &&
                AppDialog.Show(this, $"\"{group.FileName}\" has unsaved changes that will be lost. Reload anyway?", "Reload",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _changedOnDisk.Remove(group);
            UpdateDiskBanner();
            await EditHost.ReloadGroupAsync(group);
        }

        private void BannerIgnore_Click(object sender, RoutedEventArgs e)
        {
            var group = _readerGroup;
            if (group == null || EditHost == null) return;
            EditHost.IgnoreDiskChange(group);
            _changedOnDisk.Remove(group);
            UpdateDiskBanner();
        }

        // ── Bảng lệnh Ctrl+K ──────────────────────────────────────────

        private static Geometry PaletteIcon(string name) => (Geometry)Application.Current.FindResource("Ui.Icon." + name);

        private void OpenPalette()
        {
            var items = new List<PaletteItem>();
            void Cmd(string title, string shortcut, string icon, Action run) => items.Add(new PaletteItem(Loc.T("Commands"), Loc.T(title), shortcut, PaletteIcon(icon), run));

            Cmd("Open file…", "Ctrl+O", "folder", () => { if (EditHost != null) _ = EditHost.OpenFilesAsync(); });
            Cmd("Start: recent files and quick tools", "", "clock", () => ShowStart(true));
            Cmd("Merge files…", "", "merge", () => ReaderShowMergeWindow_Click(this, new RoutedEventArgs()));
            if (_readerGroup != null)
            {
                Cmd("Save", "Ctrl+S", "save", () => _ = SaveCurrentGroupAsync(saveAs: false));
                Cmd("Save as…", "Ctrl+Shift+S", "save", () => _ = SaveCurrentGroupAsync(saveAs: true));
                Cmd("Close this file", "", "close", () => ReaderCloseDocument_Click(new FrameworkElement { DataContext = _readerGroup }, new RoutedEventArgs()));
                Cmd("Actual size", "", "actual", () => ReaderActualSize_Click(this, new RoutedEventArgs()));
                Cmd("Fit page", "", "fitp", () => ReaderFitPage_Click(this, new RoutedEventArgs()));
                Cmd("Fit width", "Ctrl+0", "fitw", () => ReaderFitWidth_Click(this, new RoutedEventArgs()));
                Cmd(_readerContinuousMode ? "Switch to single page view" : "Switch to continuous scrolling", "", "scroll", () => ReaderContinuousToggle_Click(this, new RoutedEventArgs()));
                Cmd("Two-page view", "", "twopage", () => ReaderViewTwoPage_Click(this, new RoutedEventArgs()));
                Cmd("Rotate view left", "", "rotl", () => ReaderRotateLeft_Click(this, new RoutedEventArgs()));
                Cmd("Rotate view right", "", "rotr", () => ReaderRotateRight_Click(this, new RoutedEventArgs()));
                Cmd("Show pages panel", "", "pages", () => { ShowStart(false); ReaderSidePanel.ShowPanel("Pages"); });
                Cmd("Show bookmarks panel", "", "bookmark", () => { ShowStart(false); ReaderSidePanel.ShowPanel("Bookmarks"); });
                Cmd("Show layers panel", "", "layers", () => { ShowStart(false); ReaderSidePanel.ShowPanel("Layers"); });
                Cmd("Show comments panel", "", "comment", () => { ShowStart(false); ReaderSidePanel.ShowPanel("Comments"); });
            }
            Cmd("Capture the screen (XT Capture)…", "", "capture", () => CaptureLauncher.Run("capture", this));
            Cmd("Store: the screen captures kept by XT Capture", "", "captures", () => CaptureLauncher.Run("store", this));
            Cmd("Find in document…", "Ctrl+F", "search", OpenFind);
            Cmd(_crosshair ? "Crosshair off" : "Crosshair: lines through the pointer, to read a drawing", "X", "select", ToggleCrosshair);
            if (_readerGroup != null) Cmd("Export / split PDF…", "Ctrl+Shift+E", "export", () => OpenExport(preferFlatten: false));
            if (_readerGroup != null) Cmd("Print…", "Ctrl+P", "print", () => ReaderPrint_Click(this, new RoutedEventArgs()));
            if (_readerGroup != null) Cmd("OCR: read the scanned pages into a searchable copy…", "", "selecttext", StartOcr);
            if (_readerGroup != null) Cmd("Document security and permissions…", "", "lock", () => ReaderSecurity_Click(this, new RoutedEventArgs()));
            Cmd("Undo", "Ctrl+Z", "undo", () => EditHost?.Undo());
            Cmd("Redo", "Ctrl+Y", "redo", () => EditHost?.Redo());
            Cmd("Keyboard shortcuts", "F1", "info", () => Controls.KeyboardShortcutsWindow.ShowFor(this));
            Cmd("About PDF Reader Pro", "", "info", () => Controls.AboutWindow.ShowFor(this));
            Cmd("Settings", "", "settings", () => { ShowStart(false); ShowSettings(true); });
            Cmd(ThemeService.IsDark ? "Switch to the light theme" : "Switch to the dark theme", "", "eye", () =>
            {
                AppSettings.Theme = ThemeService.IsDark ? "Light" : "Dark";
                ThemeService.ApplySaved();
            });

            foreach (var group in _groups)
            {
                var g = group;
                items.Add(new PaletteItem(Loc.T("Open files"), group.FileName, "", PaletteIcon("file"), () =>
                {
                    ShowStart(false);
                    ReaderDocumentTabs.SelectedItem = g;
                }));
            }
            var open = new HashSet<string>(_groups.Select(g => g.SourcePath), StringComparer.OrdinalIgnoreCase);
            foreach (var recent in RecentFilesStore.Items.Where(r => !open.Contains(r.Path) && File.Exists(r.Path)).Take(15))
            {
                string path = recent.Path;
                items.Add(new PaletteItem(Loc.T("Recent files"), Path.GetFileName(path), "", PaletteIcon("clock"), () =>
                {
                    ShowStart(false);
                    if (EditHost != null) _ = EditHost.OpenPathsAsync(new[] { path });
                }));
            }
            Palette.Open(items, TitleSearchButton);
            Loc.ApplyTree(Palette);
        }
    }
}
