using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Services;
using DocumentGroup = XTPdfMergeApp.Domain.WorkspaceDocument;

namespace XTPdfMergeApp
{
    /// <summary>
    /// Màn hình Start (Recent, Workspaces), thanh vàng "file đổi trên đĩa" và bảng lệnh Ctrl+K của cửa sổ đọc
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
            StartPage.RestoreRequested += entry => _ = RestoreWorkspaceAsync(entry);
            StartPage.CurrentFiles = () => (_groups.Select(g => g.SourcePath).ToList(), _readerGroup?.SourcePath);
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

        private void ShowStart(bool show)
        {
            if (show == (StartPage.Visibility == Visibility.Visible)) return;
            _autoStart = show && _groups.Count == 0;
            if (show)
            {
                ShowSettings(false);
                ShowMerge(false);
                StartPage.Reload();
            }
            StartPage.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private void StartButton_Click(object sender, RoutedEventArgs e) => ShowStart(StartPage.Visibility != Visibility.Visible);

        private async Task RestoreWorkspaceAsync(WorkspaceEntry entry)
        {
            if (EditHost == null) return;
            var existing = entry.Files.Where(File.Exists).ToList();
            int missing = entry.Files.Count - existing.Count;
            ShowStart(false);
            if (existing.Count > 0) await EditHost.OpenPathsAsync(existing);
            if (entry.Active != null)
            {
                var active = _groups.FirstOrDefault(g => string.Equals(g.SourcePath, entry.Active, StringComparison.OrdinalIgnoreCase));
                if (active != null && active.Pages.Count > 0) await ShowPageAsync(active, active.Pages[0], preserveZoomMode: true);
            }
            if (missing > 0)
                MessageBox.Show(this, missing == 1 ? "1 file of this workspace was not found and was skipped." : $"{missing} files of this workspace were not found and were skipped.",
                    "Restore workspace", MessageBoxButton.OK, MessageBoxImage.Information);
        }

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
                DiskChangedText.Text = $"{_readerGroup.FileName} was changed on disk ({time:HH:mm}). What you see may be out of date.";
                DiskChangedBanner.Visibility = Visibility.Visible;
            }
            else DiskChangedBanner.Visibility = Visibility.Collapsed;
        }

        private async void BannerReload_Click(object sender, RoutedEventArgs e)
        {
            var group = _readerGroup;
            if (group == null || EditHost == null) return;
            if (group.IsDirty &&
                MessageBox.Show(this, $"\"{group.FileName}\" has unsaved changes that will be lost. Reload anyway?", "Reload",
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

        private static Geometry Icon(string name) => (Geometry)Application.Current.FindResource("Ui.Icon." + name);

        private void OpenPalette()
        {
            var items = new List<PaletteItem>();
            void Cmd(string title, string shortcut, string icon, Action run) => items.Add(new PaletteItem("Commands", title, shortcut, Icon(icon), run));

            Cmd("Open file…", "Ctrl+O", "folder", () => { if (EditHost != null) _ = EditHost.OpenFilesAsync(); });
            Cmd("Start: recent files and workspaces", "", "clock", () => ShowStart(true));
            Cmd("Merge files…", "", "merge", () => ReaderShowMergeWindow_Click(this, new RoutedEventArgs()));
            if (_readerGroup != null)
            {
                Cmd("Save", "Ctrl+S", "save", () => _ = SaveCurrentGroupAsync(saveAs: false));
                Cmd("Save as…", "Ctrl+Shift+S", "save", () => _ = SaveCurrentGroupAsync(saveAs: true));
                Cmd("Close this file", "", "close", () => ReaderCloseDocument_Click(new FrameworkElement { DataContext = _readerGroup }, new RoutedEventArgs()));
                Cmd("Fit page", "", "fitp", () => ReaderFitPage_Click(this, new RoutedEventArgs()));
                Cmd("Fit width", "Ctrl+0", "fitw", () => ReaderFitWidth_Click(this, new RoutedEventArgs()));
                Cmd(_readerContinuousMode ? "Switch to single page view" : "Switch to continuous scrolling", "", "scroll", () => ReaderContinuousToggle_Click(this, new RoutedEventArgs()));
                Cmd("Rotate view left", "", "rotl", () => ReaderRotateLeft_Click(this, new RoutedEventArgs()));
                Cmd("Rotate view right", "", "rotr", () => ReaderRotateRight_Click(this, new RoutedEventArgs()));
                Cmd("Show pages panel", "", "pages", () => { ShowStart(false); ReaderSidePanel.ShowPanel("Pages"); });
                Cmd("Show bookmarks panel", "", "bookmark", () => { ShowStart(false); ReaderSidePanel.ShowPanel("Bookmarks"); });
                Cmd("Show layers panel", "", "layers", () => { ShowStart(false); ReaderSidePanel.ShowPanel("Layers"); });
            }
            Cmd("Undo", "Ctrl+Z", "undo", () => EditHost?.Undo());
            Cmd("Redo", "Ctrl+Y", "redo", () => EditHost?.Redo());
            Cmd("Settings", "", "settings", () => { ShowStart(false); ShowSettings(true); });
            Cmd(ThemeService.IsDark ? "Switch to the light theme" : "Switch to the dark theme", "", "eye", () =>
            {
                AppSettings.Theme = ThemeService.IsDark ? "Light" : "Dark";
                ThemeService.ApplySaved();
            });

            foreach (var group in _groups)
            {
                var g = group;
                items.Add(new PaletteItem("Open files", group.FileName, "", Icon("file"), () =>
                {
                    ShowStart(false);
                    ReaderDocumentTabs.SelectedItem = g;
                }));
            }
            var open = new HashSet<string>(_groups.Select(g => g.SourcePath), StringComparer.OrdinalIgnoreCase);
            foreach (var recent in RecentFilesStore.Items.Where(r => !open.Contains(r.Path) && File.Exists(r.Path)).Take(15))
            {
                string path = recent.Path;
                items.Add(new PaletteItem("Recent files", Path.GetFileName(path), "", Icon("clock"), () =>
                {
                    ShowStart(false);
                    if (EditHost != null) _ = EditHost.OpenPathsAsync(new[] { path });
                }));
            }
            Palette.Open(items);
        }
    }
}
