using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace XTCapture
{
    /// <summary>
    /// The comment toolbar of a capture: the tools (with icon and text), undo / redo / delete, and under it the properties of what is selected or about to be drawn
    /// (colour, width, dash, opacity, fill, font). It only talks to a <see cref="MarkupController"/>, so the overlay and the capture editor use the same one.
    /// </summary>
    internal sealed class MarkupToolbar
    {
        private readonly MarkupController _controller;
        private readonly Dictionary<MarkupTool, Border> _toolButtons = new();
        private Border? _undo, _redo, _delete;
        private readonly List<(Border Swatch, string Hex)> _swatches = new();
        private readonly ComboBox _width = Combo("Line width", 70), _dash = Combo("Line style", 84), _opacity = Combo("Opacity", 72), _fill = Combo("Fill colour", 112),
            _fontSize = Combo("Text size", 70), _font = Combo("Font", 130), _block = Combo("Block size", 76);
        private Border? _bold, _italic, _underline;
        private readonly Dictionary<string, FrameworkElement> _groups = new();
        private bool _loading;

        public Border ToolsBar { get; }
        public Border PropertyBar { get; }

        public MarkupToolbar(MarkupController controller)
        {
            _controller = controller;
            ToolsBar = BuildTools();
            PropertyBar = BuildProperties();
            controller.Changed += Refresh;
            Refresh();
        }

        // ── Pieces ──────────────────────────────────────────────────────

        private static Brush Themed(string key, Brush fallback) => Application.Current?.TryFindResource(key) as Brush ?? fallback;
        private static Brush TextBrush => Themed("Ui.Text", new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37)));

        private static ComboBox Combo(string tip, double width) => new() { Width = width, Height = 28, Margin = new Thickness(3, 0, 3, 0), ToolTip = tip, Focusable = false, VerticalAlignment = VerticalAlignment.Center };

        private static Border Frame(Panel content) => new()
        {
            Background = Themed("Ui.Surface", Brushes.White), BorderBrush = Themed("Ui.Border", Brushes.LightGray), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4), Child = content, Cursor = Cursors.Arrow,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.3, Direction = 270 }
        };

        private Border Button(string icon, string text, string tip, Action click)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(CaptureIcons.Create(icon, TextBrush));
            if (text.Length > 0) content.Children.Add(new TextBlock { Text = text, FontSize = 12.5, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = TextBrush });
            var border = new Border { Padding = new Thickness(text.Length > 0 ? 8 : 7, 5, text.Length > 0 ? 8 : 7, 5), Margin = new Thickness(1), CornerRadius = new CornerRadius(5), Cursor = Cursors.Hand, ToolTip = tip, Child = content, Background = Brushes.Transparent };
            border.MouseEnter += (_, _) => { if (border.Tag as string != "on") border.Background = Themed("Ui.Hover", new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB))); };
            border.MouseLeave += (_, _) => { if (border.Tag as string != "on") border.Background = Brushes.Transparent; };
            border.MouseLeftButtonDown += (_, e) => e.Handled = true;
            border.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
            return border;
        }

        private Border BuildTools()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            void Tool(MarkupTool tool, string icon, string text, string tip)
            {
                var button = Button(icon, text, tip, () => _controller.SetTool(tool));
                _toolButtons[tool] = button;
                row.Children.Add(button);
            }
            Tool(MarkupTool.Select, CaptureIcons.SelectTool, "Select", "Select, move and change what was drawn (V)");
            Tool(MarkupTool.Rectangle, CaptureIcons.Rectangle, "Rect", "Rectangle");
            Tool(MarkupTool.Ellipse, CaptureIcons.Circle, "Circle", "Circle / ellipse");
            Tool(MarkupTool.Arrow, CaptureIcons.ArrowTool, "Arrow", "Arrow");
            Tool(MarkupTool.Line, CaptureIcons.LineTool, "Line", "Line");
            Tool(MarkupTool.Pen, CaptureIcons.PenTool, "Pen", "Free drawing");
            Tool(MarkupTool.Text, CaptureIcons.TextTool, "Text", "Text: click where it goes, type, Enter to finish");
            Tool(MarkupTool.Marker, CaptureIcons.NumberTool, "Number", "Numbered marker 1, 2, 3 ...");
            Tool(MarkupTool.Mosaic, CaptureIcons.MosaicTool, "Mosaic", "Hide an area behind blocks");
            row.Children.Add(new Border { Width = 1, Margin = new Thickness(4, 4, 4, 4), Background = Themed("Ui.Border", Brushes.LightGray) });
            _undo = Button(CaptureIcons.Undo, "", "Undo (Ctrl+Z)", _controller.Undo);
            _redo = Button(CaptureIcons.Redo, "", "Redo (Ctrl+Y)", _controller.Redo);
            _delete = Button(CaptureIcons.Trash, "", "Delete the selected object (Delete)", _controller.DeleteSelected);
            row.Children.Add(_undo);
            row.Children.Add(_redo);
            row.Children.Add(_delete);
            return Frame(row);
        }

        private Border BuildProperties()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            FrameworkElement Group(string name, params FrameworkElement[] parts)
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                foreach (var part in parts) panel.Children.Add(part);
                _groups[name] = panel;
                row.Children.Add(panel);
                return panel;
            }

            var colors = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            foreach (string hex in MarkupStyle.Colors)
            {
                string captured = hex;
                var dot = new Ellipse { Width = 14, Height = 14, Fill = MarkupRenderer.BrushFor(hex), Stroke = Brushes.Gray, StrokeThickness = hex == "#FFFFFF" ? 1 : 0 };
                var swatch = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(1), Child = dot, Cursor = Cursors.Hand, ToolTip = "Colour", BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, Background = Brushes.Transparent };
                swatch.MouseLeftButtonDown += (_, e) => e.Handled = true;
                swatch.MouseLeftButtonUp += (_, e) => { e.Handled = true; _controller.ApplyStyle(s => s with { Color = captured }); };
                _swatches.Add((swatch, hex));
                colors.Children.Add(swatch);
            }
            Group("color", colors);

            foreach (double width in MarkupStyle.Widths) _width.Items.Add(new ComboBoxItem { Content = width.ToString("0") + " px", Tag = width, Focusable = false });
            _width.SelectionChanged += (_, _) => { if (!_loading && _width.SelectedItem is ComboBoxItem { Tag: double w }) _controller.ApplyStyle(s => s with { Width = w }); };
            Group("width", _width);

            foreach (string name in new[] { "Solid", "Dashed", "Dotted" }) _dash.Items.Add(new ComboBoxItem { Content = name, Tag = _dash.Items.Count, Focusable = false });
            _dash.SelectionChanged += (_, _) => { if (!_loading && _dash.SelectedItem is ComboBoxItem { Tag: int d }) _controller.ApplyStyle(s => s with { Dash = d }); };
            Group("dash", _dash);

            foreach (int opacity in MarkupStyle.Opacities) _opacity.Items.Add(new ComboBoxItem { Content = opacity + "%", Tag = opacity, Focusable = false });
            _opacity.SelectionChanged += (_, _) => { if (!_loading && _opacity.SelectedItem is ComboBoxItem { Tag: int o }) _controller.ApplyStyle(s => s with { Opacity = o }); };
            Group("opacity", _opacity);

            foreach (var (name, hex) in MarkupStyle.Fills)
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal };
                item.Children.Add(new Ellipse { Width = 12, Height = 12, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Fill = hex.Length > 0 ? MarkupRenderer.BrushFor(hex) : Brushes.Transparent, Stroke = Brushes.Gray, StrokeThickness = 1 });
                item.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
                _fill.Items.Add(new ComboBoxItem { Content = item, Tag = hex, Focusable = false });
            }
            _fill.SelectionChanged += (_, _) => { if (!_loading && _fill.SelectedItem is ComboBoxItem { Tag: string f }) _controller.ApplyStyle(s => s with { Fill = f }); };
            Group("fill", _fill);

            foreach (string font in MarkupStyle.Fonts) _font.Items.Add(new ComboBoxItem { Content = font, FontFamily = new FontFamily(font), Tag = font, Focusable = false });
            _font.SelectionChanged += (_, _) => { if (!_loading && _font.SelectedItem is ComboBoxItem { Tag: string f }) _controller.ApplyStyle(s => s with { Font = f }); };
            foreach (double size in MarkupStyle.FontSizes) _fontSize.Items.Add(new ComboBoxItem { Content = size.ToString("0"), Tag = size, Focusable = false });
            _fontSize.SelectionChanged += (_, _) => { if (!_loading && _fontSize.SelectedItem is ComboBoxItem { Tag: double z }) _controller.ApplyStyle(s => s with { FontSize = z }); };
            _bold = Toggle("B", FontWeights.Bold, FontStyles.Normal, "Bold", () => _controller.ApplyStyle(s => s with { Bold = !s.Bold }));
            _italic = Toggle("I", FontWeights.Normal, FontStyles.Italic, "Italic", () => _controller.ApplyStyle(s => s with { Italic = !s.Italic }));
            _underline = Toggle("U", FontWeights.Normal, FontStyles.Normal, "Underline", () => _controller.ApplyStyle(s => s with { Underline = !s.Underline }), underline: true);
            Group("fontFamily", _font);
            Group("fontSize", _fontSize);
            Group("fontStyle", _bold, _italic, _underline);

            foreach (double block in new double[] { 8, 12, 16, 24, 32 }) _block.Items.Add(new ComboBoxItem { Content = block.ToString("0") + " px", Tag = block, Focusable = false });
            _block.SelectionChanged += (_, _) => { if (!_loading && _block.SelectedItem is ComboBoxItem { Tag: double b }) _controller.ApplyStyle(s => s with { Width = b }); };
            Group("block", _block);
            return Frame(row);
        }

        private Border Toggle(string text, FontWeight weight, FontStyle style, string tip, Action click, bool underline = false)
        {
            var label = new TextBlock { Text = text, FontWeight = weight, FontStyle = style, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = TextBrush, TextDecorations = underline ? System.Windows.TextDecorations.Underline : null };
            var border = new Border { Width = 26, Height = 26, Margin = new Thickness(1), CornerRadius = new CornerRadius(5), Cursor = Cursors.Hand, ToolTip = tip, Child = label, Background = Brushes.Transparent };
            border.MouseLeftButtonDown += (_, e) => e.Handled = true;
            border.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
            return border;
        }

        // ── State ───────────────────────────────────────────────────────

        /// <summary>The kind whose properties the bar shows: the selected object's, else the tool's. Null with the Select tool and nothing selected.</summary>
        internal MarkupKind? FocusKind => _controller.Selected?.Kind ?? _controller.Tool switch
        {
            MarkupTool.Rectangle => MarkupKind.Rectangle, MarkupTool.Ellipse => MarkupKind.Ellipse, MarkupTool.Arrow => MarkupKind.Arrow, MarkupTool.Line => MarkupKind.Line,
            MarkupTool.Pen => MarkupKind.Pen, MarkupTool.Text => MarkupKind.Text, MarkupTool.Marker => MarkupKind.Marker, MarkupTool.Mosaic => MarkupKind.Mosaic, _ => (MarkupKind?)null
        };

        /// <summary>The property groups shown for a kind.</summary>
        internal static IReadOnlyCollection<string> GroupsFor(MarkupKind kind) => kind switch
        {
            MarkupKind.Rectangle or MarkupKind.Ellipse => new[] { "color", "width", "dash", "opacity", "fill" },
            MarkupKind.Arrow or MarkupKind.Line => new[] { "color", "width", "dash", "opacity" },
            MarkupKind.Pen => new[] { "color", "width", "opacity" },
            MarkupKind.Text => new[] { "color", "fontFamily", "fontSize", "fontStyle", "opacity" },
            MarkupKind.Marker => new[] { "color", "fontSize" },
            MarkupKind.Mosaic => new[] { "block" },
            _ => Array.Empty<string>()
        };

        internal IEnumerable<string> VisibleGroups => _groups.Where(g => g.Value.Visibility == Visibility.Visible).Select(g => g.Key);
        internal bool IsToolOn(MarkupTool tool) => _toolButtons[tool].Tag as string == "on";

        public void Refresh()
        {
            _loading = true;
            try
            {
                foreach (var (tool, button) in _toolButtons)
                {
                    bool on = tool == _controller.Tool;
                    button.Tag = on ? "on" : null;
                    button.Background = on ? Themed("Ui.Hover", new SolidColorBrush(Color.FromRgb(0xDB, 0xEA, 0xFE))) : Brushes.Transparent;
                }
                SetEnabled(_undo, _controller.CanUndo);
                SetEnabled(_redo, _controller.CanRedo);
                SetEnabled(_delete, _controller.Selected != null);

                var kind = FocusKind;
                var wanted = kind is { } k ? GroupsFor(k) : Array.Empty<string>();
                foreach (var (name, group) in _groups) group.Visibility = wanted.Contains(name) ? Visibility.Visible : Visibility.Collapsed;
                PropertyBar.Visibility = wanted.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                // a marker only offers its size, and its group is called size
                var style = _controller.Style;
                foreach (var (swatch, hex) in _swatches) swatch.BorderBrush = string.Equals(hex, style.Color, StringComparison.OrdinalIgnoreCase) ? Themed("Ui.Accent", Brushes.DodgerBlue) : Brushes.Transparent;
                Select(_width, MarkupStyle.Widths.ToList().FindIndex(w => Math.Abs(w - style.Width) < 0.01));
                Select(_dash, Math.Clamp(style.Dash, 0, 2));
                Select(_opacity, Array.IndexOf(MarkupStyle.Opacities, style.Opacity));
                Select(_fill, Array.FindIndex(MarkupStyle.Fills, f => string.Equals(f.Hex, style.Fill, StringComparison.OrdinalIgnoreCase)));
                Select(_font, Array.FindIndex(MarkupStyle.Fonts, f => f == style.Font));
                Select(_fontSize, Array.FindIndex(MarkupStyle.FontSizes, z => Math.Abs(z - style.FontSize) < 0.01));
                Select(_block, new double[] { 8, 12, 16, 24, 32 }.ToList().FindIndex(b => Math.Abs(b - style.Width) < 0.01));
                Mark(_bold, style.Bold);
                Mark(_italic, style.Italic);
                Mark(_underline, style.Underline);
            }
            finally { _loading = false; }
        }

        private static void Select(ComboBox box, int index) => box.SelectedIndex = index;

        private static void SetEnabled(Border? button, bool enabled)
        {
            if (button == null) return;
            button.IsEnabled = enabled;
            button.Opacity = enabled ? 1 : 0.4;
        }

        private static void Mark(Border? toggle, bool on)
        {
            if (toggle != null) toggle.Background = on ? Themed("Ui.Hover", new SolidColorBrush(Color.FromRgb(0xDB, 0xEA, 0xFE))) : Brushes.Transparent;
        }
    }
}
