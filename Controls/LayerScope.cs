using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using XTPdfMergeApp.Services;

namespace XTPdfMergeApp.Controls
{
    /// <summary>1 layer (OCG) của 1 file nguồn. Locked / Usage: không đổi được hiện-ẩn khi xem.</summary>
    internal readonly record struct LayerEntry(string Path, string Id, bool Locked, bool Usage)
    {
        public bool Toggleable => !Locked && !Usage;
    }

    /// <summary>1 dòng trong cây Layer đã gộp: layer CÙNG TÊN của nhiều file là 1 dòng (mỗi file 1 <see cref="LayerEntry"/>).
    /// Nhóm (nhãn trong /Order, không phải OCG) không có Entries — trạng thái của nhóm suy ra từ các layer con.</summary>
    internal sealed class LayerNode : INotifyPropertyChanged
    {
        public const string LockedToolTip = "This layer is locked; its visibility cannot be changed";
        public const string UsageToolTip = "This layer is controlled by the file's /AS (usage) rules while viewing; its visibility cannot be changed";

        internal LayerNode(string title, bool isGroup, LayerNode? parent)
        {
            Title = title;
            IsGroup = isGroup;
            Parent = parent;
            Depth = parent == null ? 0 : parent.Depth + 1;
        }

        public string Title { get; }
        public bool IsGroup { get; }
        public LayerNode? Parent { get; }
        public int Depth { get; }
        public List<LayerNode> Children { get; } = new();
        public List<LayerEntry> Entries { get; } = new();

        public bool HasChildren => Children.Count > 0;
        public Visibility ArrowVisibility => HasChildren ? Visibility.Visible : Visibility.Hidden;
        public Thickness Indent => new(6 + Depth * 18, 0, 8, 0);
        public FontWeight TitleWeight => IsGroup ? FontWeights.SemiBold : FontWeights.Normal;

        public int FileCount => Entries.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        public string? ChipText => FileCount > 1 ? FileCount + " files" : null;
        public Visibility ChipVisibility => FileCount > 1 ? Visibility.Visible : Visibility.Collapsed;

        public bool CanToggle => IsGroup ? Subtree().Any(n => n.Entries.Any(e => e.Toggleable)) : Entries.Any(e => e.Toggleable);

        public string? ToolTipText
        {
            get
            {
                if (IsGroup || Entries.Count == 0 || CanToggle) return null;
                return Entries.All(e => e.Locked) ? LockedToolTip : UsageToolTip;
            }
        }

        /// <summary>Chính nó và mọi nút con cháu.</summary>
        public IEnumerable<LayerNode> Subtree()
        {
            yield return this;
            foreach (var child in Children)
                foreach (var node in child.Subtree())
                    yield return node;
        }

        private bool? _state = true;
        /// <summary>true = hiện, false = ẩn, null = lẫn (nhóm có con khác trạng thái, hoặc layer cùng tên khác trạng thái giữa các file).</summary>
        public bool? State
        {
            get => _state;
            internal set { if (_state == value) return; _state = value; Notify(); Notify(nameof(IsDimmed)); }
        }

        public bool IsDimmed => _state == false;

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set { if (_isExpanded == value) return; _isExpanded = value; Notify(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Cây layer của các file trong 1 window (layer cùng tên gộp thành 1 dòng — giống khi lưu file ghép) và mọi phép đổi
    /// hiện/ẩn. Các hàm <c>With…</c> KHÔNG đổi trạng thái: trả về tập "layer đang tắt" mới theo từng file, người gọi
    /// <see cref="Commit"/> rồi báo cho ReaderWindow đổi thật.
    /// </summary>
    internal sealed class LayerScope
    {
        private readonly Dictionary<string, PdfLayerInfo> _infos = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, HashSet<string>> _hidden = new(StringComparer.OrdinalIgnoreCase);

        public List<LayerNode> Roots { get; } = new();
        public IReadOnlyCollection<string> Paths => _infos.Keys;

        /// <summary>Số layer sau khi gộp (dòng có ít nhất 1 OCG).</summary>
        public int LayerCount => AllNodes().Count(n => n.Entries.Count > 0);
        public int VisibleCount => AllNodes().Count(n => n.Entries.Count > 0 && n.State != false);

        public LayerScope(IEnumerable<(string Path, PdfLayerInfo Info)> files, Func<string, IReadOnlySet<string>> currentHidden)
        {
            foreach (var (path, info) in files)
            {
                if (!info.HasLayers || _infos.ContainsKey(path)) continue;
                _infos[path] = info;
                _hidden[path] = new HashSet<string>(currentHidden(path));
                Merge(Roots, info.Roots, path, info, null);
            }
            int total = AllNodes().Count();
            foreach (var node in AllNodes()) node.IsExpanded = node.HasChildren && (total <= 300 || node.Depth == 0);
            RefreshStates();
        }

        public bool HasLayers => _infos.Count > 0;

        public PdfLayerInfo InfoOf(string path) => _infos[path];

        private static void Merge(List<LayerNode> target, IReadOnlyList<PdfLayerNode> source, string path, PdfLayerInfo info, LayerNode? parent)
        {
            foreach (var s in source)
            {
                bool group = s.OcgId == null;
                var node = target.FirstOrDefault(n => n.IsGroup == group && string.Equals(n.Title, s.Title, StringComparison.Ordinal));
                if (node == null)
                {
                    node = new LayerNode(s.Title, group, parent);
                    target.Add(node);
                }
                if (!group)
                {
                    var entry = new LayerEntry(path, s.OcgId!, info.Locked.Contains(s.OcgId!), s.IsUsageControlled);
                    if (!node.Entries.Contains(entry)) node.Entries.Add(entry);
                }
                Merge(node.Children, s.Children, path, info, node);
            }
        }

        private IEnumerable<LayerNode> AllNodes() => Roots.SelectMany(r => r.Subtree());

        // ── Trạng thái hiện tại ───────────────────────────────────────

        private bool IsVisible(LayerEntry e) => !_hidden[e.Path].Contains(e.Id);

        private bool? Compute(LayerNode node)
        {
            var entries = node.IsGroup ? node.Subtree().SelectMany(n => n.Entries) : node.Entries;
            bool anyVisible = false, anyHidden = false;
            foreach (var e in entries)
            {
                if (IsVisible(e)) anyVisible = true; else anyHidden = true;
            }
            if (!anyVisible && !anyHidden) return true;
            return anyVisible && anyHidden ? null : anyVisible;
        }

        public void RefreshStates()
        {
            foreach (var node in AllNodes()) node.State = Compute(node);
        }

        /// <summary>Trạng thái layer đổi từ nơi khác (Undo, mở lại file…): đọc lại từ kho trạng thái.</summary>
        public void SyncFrom(Func<string, IReadOnlySet<string>> currentHidden)
        {
            foreach (string path in _infos.Keys.ToList()) _hidden[path] = new HashSet<string>(currentHidden(path));
            RefreshStates();
        }

        // ── Phép đổi (trả về tập tắt mới của MỌI file; chưa áp dụng) ───

        public Dictionary<string, HashSet<string>> CloneHidden()
            => _hidden.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value), StringComparer.OrdinalIgnoreCase);

        private static void Set(Dictionary<string, HashSet<string>> hidden, LayerEntry e, bool visible)
        {
            if (visible) hidden[e.Path].Remove(e.Id); else hidden[e.Path].Add(e.Id);
        }

        private static IEnumerable<LayerEntry> Toggleable(LayerNode node)
            => (node.IsGroup ? node.Subtree().SelectMany(n => n.Entries) : node.Entries).Where(e => e.Toggleable);

        /// <summary>Bấm ô chọn: đang hiện hết → ẩn; ngược lại (ẩn hoặc lẫn) → hiện hết. Nhóm áp cho mọi layer bên trong.</summary>
        public Dictionary<string, HashSet<string>> WithToggle(LayerNode node)
        {
            bool show = node.State != true;
            var hidden = CloneHidden();
            foreach (var e in Toggleable(node)) Set(hidden, e, show);
            return hidden;
        }

        public Dictionary<string, HashSet<string>> WithAll(bool visible)
        {
            var hidden = CloneHidden();
            foreach (var e in AllNodes().SelectMany(n => n.Entries).Where(e => e.Toggleable)) Set(hidden, e, visible);
            return hidden;
        }

        /// <summary>Chỉ hiện layer này (và các layer con của nó), ẩn tất cả layer khác.</summary>
        public Dictionary<string, HashSet<string>> WithIsolate(LayerNode node)
        {
            var hidden = CloneHidden();
            var keep = node.Subtree().SelectMany(n => n.Entries).ToHashSet();
            foreach (var e in AllNodes().SelectMany(n => n.Entries).Where(e => e.Toggleable))
                Set(hidden, e, keep.Contains(e));
            return hidden;
        }

        /// <summary>Về đúng trạng thái mặc định lưu trong từng file.</summary>
        public Dictionary<string, HashSet<string>> WithDefault()
            => _infos.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value.DefaultHidden), StringComparer.OrdinalIgnoreCase);

        /// <summary>Áp View đã lưu: tên trong View hiện/ẩn theo View, tên khác giữ mặc định của file.</summary>
        public Dictionary<string, HashSet<string>> WithView(LayerView view)
        {
            var visible = new HashSet<string>(view.Visible, StringComparer.Ordinal);
            var hiddenNames = new HashSet<string>(view.Hidden, StringComparer.Ordinal);
            var result = CloneHidden();
            foreach (var (path, info) in _infos)
            {
                foreach (var (id, name) in info.Names)
                {
                    var entry = new LayerEntry(path, id, info.Locked.Contains(id), info.UsageControlled?.Contains(id) == true);
                    if (!entry.Toggleable) continue;
                    if (visible.Contains(name)) Set(result, entry, true);
                    else if (hiddenNames.Contains(name)) Set(result, entry, false);
                    else Set(result, entry, !info.DefaultHidden.Contains(id));
                }
            }
            return result;
        }

        /// <summary>View có nhắc tới ít nhất 1 layer của các file này không (View không liên quan thì không thể là "View hiện tại").</summary>
        public bool Touches(LayerView view)
        {
            var names = new HashSet<string>(view.Visible.Concat(view.Hidden), StringComparer.Ordinal);
            return _infos.Values.Any(info => info.Names.Values.Any(names.Contains));
        }

        /// <summary>Đặt <paramref name="hidden"/> làm trạng thái hiện tại; trả về các file có thay đổi (để đổi thật).</summary>
        public List<(string Path, PdfLayerInfo Info, HashSet<string> Hidden)> Commit(Dictionary<string, HashSet<string>> hidden)
        {
            var changed = new List<(string, PdfLayerInfo, HashSet<string>)>();
            foreach (var (path, set) in hidden)
            {
                if (!_hidden.TryGetValue(path, out var old) || old.SetEquals(set)) continue;
                _hidden[path] = set;
                changed.Add((path, _infos[path], set));
            }
            RefreshStates();
            return changed;
        }

        public bool IsCurrent(Dictionary<string, HashSet<string>> hidden)
            => hidden.All(kv => _hidden.TryGetValue(kv.Key, out var cur) && cur.SetEquals(kv.Value));

        /// <summary>Chụp trạng thái hiện tại thành View (theo tên; tên lẫn hiện/ẩn giữa các file thì bỏ qua).</summary>
        public LayerView Snapshot(string name)
        {
            var visible = new HashSet<string>(StringComparer.Ordinal);
            var hidden = new HashSet<string>(StringComparer.Ordinal);
            var byName = new Dictionary<string, (bool AnyVisible, bool AnyHidden)>(StringComparer.Ordinal);
            foreach (var (path, info) in _infos)
            {
                foreach (var (id, layerName) in info.Names)
                {
                    bool isVisible = !_hidden[path].Contains(id);
                    byName.TryGetValue(layerName, out var flags);
                    byName[layerName] = (flags.AnyVisible || isVisible, flags.AnyHidden || !isVisible);
                }
            }
            foreach (var (layerName, flags) in byName)
            {
                if (flags.AnyVisible && !flags.AnyHidden) visible.Add(layerName);
                else if (flags.AnyHidden && !flags.AnyVisible) hidden.Add(layerName);
            }
            return new LayerView(name, visible.OrderBy(n => n, StringComparer.Ordinal).ToList(), hidden.OrderBy(n => n, StringComparer.Ordinal).ToList());
        }

        /// <summary>Tên layer đang tắt (ở ít nhất 1 file) — dùng khi xuất file theo View.</summary>
        public IReadOnlySet<string> HiddenNames()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (path, info) in _infos)
                foreach (var id in _hidden[path])
                    if (info.Names.TryGetValue(id, out var name)) names.Add(name);
            return names;
        }

        /// <summary>Số layer (sau gộp) hiện ra nếu áp View — cho dòng phụ "N visible" trong menu.</summary>
        public int VisibleCountWith(Dictionary<string, HashSet<string>> hidden)
            => AllNodes().Where(n => n.Entries.Count > 0).Count(n => n.Entries.Any(e => !hidden[e.Path].Contains(e.Id)));

        // ── Danh sách phẳng để hiển thị ───────────────────────────────

        /// <summary>Các dòng đang hiện (theo trạng thái mở/đóng, hoặc theo ô tìm: dòng khớp + tổ tiên + con cháu của dòng khớp).</summary>
        public List<LayerNode> Flatten(string? filter)
        {
            var list = new List<LayerNode>();
            bool filtering = !string.IsNullOrWhiteSpace(filter);
            string text = filter?.Trim() ?? "";

            bool Matches(LayerNode n) => n.Title.Contains(text, StringComparison.OrdinalIgnoreCase);
            bool DescendantMatches(LayerNode n) => n.Children.Any(c => Matches(c) || DescendantMatches(c));

            void Add(LayerNode node, bool ancestorMatched)
            {
                bool self = filtering && Matches(node);
                if (filtering && !ancestorMatched && !self && !DescendantMatches(node)) return;
                list.Add(node);
                if (!filtering && !node.IsExpanded) return;
                foreach (var child in node.Children) Add(child, ancestorMatched || self);
            }

            foreach (var root in Roots) Add(root, false);
            return list;
        }
    }
}
