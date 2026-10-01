using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XTPdfMergeApp.Services;

namespace XTPdfMergeApp.Controls
{
    /// <summary>
    /// Tab Layers của panel trái: cây layer của MỌI file trong window đang xem (layer cùng tên gộp 1 dòng), ô tìm, Show/Hide all,
    /// Isolate/Reset và các View đã lưu. Panel không đổi trạng thái thật — báo <see cref="HiddenChanged"/> cho ReaderWindow.
    /// </summary>
    public partial class LayersPanel : UserControl
    {
        private LayerScope? _scope;
        private int _generation;

        public LayersPanel()
        {
            InitializeComponent();
            UpdateViewButton();
        }

        /// <summary>Tập layer đang tắt của 1 file cần đổi: (file, thông tin layer của file, tập tắt mới).</summary>
        internal event Action<string, PdfLayerInfo, IReadOnlySet<string>>? HiddenChanged;
        /// <summary>Xuất PDF theo View hiện tại: (tên layer đang tắt, tên View hiện tại).</summary>
        internal event Action<IReadOnlySet<string>, string>? ExportViewRequested;
        /// <summary>Số layer sau khi gộp (null = không có layer).</summary>
        internal event Action<int?>? CountChanged;

        private Window? OwnerWindow => Window.GetWindow(this);

        /// <summary>Nạp layer của các file (thứ tự = thứ tự xuất hiện trong window). <paramref name="read"/> có cache ở người gọi.</summary>
        internal async Task SetFilesAsync(IReadOnlyList<string> paths, Func<string, Task<PdfLayerInfo>> read)
        {
            int generation = ++_generation;
            var infos = await Task.WhenAll(paths.Select(async p => (Path: p, Info: await read(p))));
            if (generation != _generation) return;

            var byPath = infos.ToDictionary(i => i.Path, i => i.Info, StringComparer.OrdinalIgnoreCase);
            _scope = new LayerScope(infos, p => PdfLayerStateStore.GetHiddenOverride(p, out _) ?? byPath[p].DefaultHidden);
            if (!_scope.HasLayers)
            {
                ContentRoot.Visibility = Visibility.Collapsed;
                EmptyText.Text = paths.Count > 1 ? "These documents have no layers." : "This document has no layers.";
                EmptyText.Visibility = Visibility.Visible;
                CountChanged?.Invoke(null);
                return;
            }
            ContentRoot.Visibility = Visibility.Visible;
            EmptyText.Visibility = Visibility.Collapsed;
            SearchBox.Text = "";
            Rebuild();
            CountChanged?.Invoke(_scope.LayerCount);
        }

        /// <summary>Không có gì để hiện (chưa mở file).</summary>
        internal void Clear(string message)
        {
            _generation++;
            _scope = null;
            ContentRoot.Visibility = Visibility.Collapsed;
            EmptyText.Text = message;
            EmptyText.Visibility = Visibility.Visible;
            CountChanged?.Invoke(null);
        }

        /// <summary>Trạng thái layer đổi từ nơi khác (Undo, mở lại file…): đọc lại từ kho trạng thái.</summary>
        internal void SyncFromStore()
        {
            if (_scope == null) return;
            var scope = _scope;
            scope.SyncFrom(p => PdfLayerStateStore.GetHiddenOverride(p, out _) ?? scope.InfoOf(p).DefaultHidden);
            UpdateViewButton();
        }

        private void Rebuild()
        {
            if (_scope == null) return;
            var selected = Rows.SelectedItem as LayerNode;
            var rows = _scope.Flatten(SearchBox.Text);
            Rows.ItemsSource = rows;
            if (selected != null && rows.Contains(selected)) Rows.SelectedItem = selected;
            UpdateViewButton();
            UpdateIsolateButton();
        }

        // ── Bấm ô chọn / mũi tên ─────────────────────────────────────

        private void Cb_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_scope == null || (sender as FrameworkElement)?.DataContext is not LayerNode { CanToggle: true } node) return;
            Apply(_scope.WithToggle(node));
        }

        private void Arrow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if ((sender as FrameworkElement)?.DataContext is not LayerNode node) return;
            node.IsExpanded = !node.IsExpanded;
            Rebuild();
        }

        private void Apply(Dictionary<string, HashSet<string>> hidden)
        {
            if (_scope == null) return;
            foreach (var (path, info, set) in _scope.Commit(hidden))
                HiddenChanged?.Invoke(path, info, set);
            UpdateViewButton();
        }

        // ── Thanh công cụ ────────────────────────────────────────────

        private void ShowAll_Click(object sender, RoutedEventArgs e) { if (_scope != null) Apply(_scope.WithAll(true)); }
        private void HideAll_Click(object sender, RoutedEventArgs e) { if (_scope != null) Apply(_scope.WithAll(false)); }
        private void Reset_Click(object sender, RoutedEventArgs e) { if (_scope != null) Apply(_scope.WithDefault()); }

        private void Isolate_Click(object sender, RoutedEventArgs e)
        {
            if (_scope != null && Rows.SelectedItem is LayerNode node) Apply(_scope.WithIsolate(node));
        }

        private void Rows_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateIsolateButton();

        private void UpdateIsolateButton()
            => IsolateButton.IsEnabled = Rows.SelectedItem is LayerNode node && node.Subtree().Any(n => n.Entries.Count > 0);

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ClearSearchButton.Visibility = SearchBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            Rebuild();
        }

        private void ClearSearch_Click(object sender, RoutedEventArgs e) => SearchBox.Text = "";

        private void Expand_Click(object sender, RoutedEventArgs e)
        {
            if (_scope == null) return;
            var groups = _scope.Roots.SelectMany(r => r.Subtree()).Where(n => n.HasChildren).ToList();
            bool expand = groups.Any(n => !n.IsExpanded);
            foreach (var n in groups) n.IsExpanded = expand;
            Rebuild();
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            if (_scope != null) ExportViewRequested?.Invoke(_scope.HiddenNames(), CurrentViewName());
        }

        // ── View đã lưu ──────────────────────────────────────────────

        private const string DefaultViewName = "Default (as saved in file)";
        private const string AllLayersName = "All layers";
        private const string CustomName = "Custom";

        /// <summary>Tên View đang khớp đúng trạng thái hiện tại, hoặc "Custom".</summary>
        private string CurrentViewName()
        {
            if (_scope == null) return CustomName;
            foreach (var view in LayerViewStore.Views)
                if (_scope.Touches(view) && _scope.IsCurrent(_scope.WithView(view))) return view.Name;
            if (_scope.IsCurrent(_scope.WithAll(true))) return AllLayersName;
            if (_scope.IsCurrent(_scope.WithDefault())) return DefaultViewName;
            return CustomName;
        }

        private void UpdateViewButton() => ViewButton.Content = CurrentViewName();

        private void ViewButton_Click(object sender, RoutedEventArgs e)
        {
            if (_scope == null) return;
            var scope = _scope;
            string current = CurrentViewName();
            var menu = new ContextMenu { PlacementTarget = ViewButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, MinWidth = ViewButton.ActualWidth };

            MenuItem Entry(string name, Dictionary<string, HashSet<string>> target)
            {
                var item = new MenuItem
                {
                    Header = name,
                    InputGestureText = $"{scope.VisibleCountWith(target)} visible",
                    IsChecked = name == current,
                    FontWeight = name == current ? FontWeights.SemiBold : FontWeights.Normal
                };
                item.Click += (_, _) => Apply(target);
                return item;
            }

            menu.Items.Add(Entry(DefaultViewName, scope.WithDefault()));
            menu.Items.Add(Entry(AllLayersName, scope.WithAll(true)));
            if (LayerViewStore.Views.Count > 0) menu.Items.Add(new Separator());
            foreach (var view in LayerViewStore.Views) menu.Items.Add(Entry(view.Name, scope.WithView(view)));
            menu.Items.Add(new Separator());

            var save = new MenuItem { Header = "Save current as new view…" };
            save.Click += (_, _) => SaveView_Click(this, e);
            menu.Items.Add(save);
            if (LayerViewStore.Views.Any(v => v.Name == current))
            {
                var update = new MenuItem { Header = $"Update “{current}” with current layers" };
                update.Click += (_, _) => { LayerViewStore.Upsert(scope.Snapshot(current)); UpdateViewButton(); };
                menu.Items.Add(update);
            }
            var manage = new MenuItem { Header = "Manage views…" };
            manage.Click += (_, _) =>
            {
                new ManageLayerViewsWindow { Owner = OwnerWindow }.ShowDialog();
                UpdateViewButton();
            };
            menu.Items.Add(manage);
            menu.IsOpen = true;
        }

        private void SaveView_Click(object sender, RoutedEventArgs e)
        {
            if (_scope == null) return;
            string? name = TextPromptWindow.Ask(OwnerWindow, "Save layer view", "Name for this view:", "");
            if (name == null) return;
            if (name is DefaultViewName or AllLayersName)
            {
                AppDialog.Show(OwnerWindow, "That name is reserved. Choose a different name.", "Save layer view", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (LayerViewStore.Exists(name) &&
                AppDialog.Show(OwnerWindow, $"A view named \"{name}\" already exists. Replace it?", "Save layer view",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            LayerViewStore.Upsert(_scope.Snapshot(name));
            UpdateViewButton();
        }
    }
}
