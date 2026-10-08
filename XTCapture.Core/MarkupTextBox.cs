using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace XTCapture
{
    /// <summary>The box a text object is typed in, placed where the text will be (in a canvas of picture pixels). Enter finishes, Shift+Enter starts a new line, Esc cancels.</summary>
    internal sealed class MarkupTextBox
    {
        private readonly Canvas _host;
        private readonly MarkupController _controller;
        private readonly TextBox _box = new();
        private string? _id;
        private bool _closing;

        public MarkupTextBox(Canvas host, MarkupController controller)
        {
            _host = host;
            _controller = controller;
            _box.AcceptsReturn = true;
            _box.Padding = new Thickness(0);
            _box.BorderThickness = new Thickness(1);
            _box.BorderBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
            _box.Background = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255));
            _box.Visibility = Visibility.Collapsed;
            _box.PreviewKeyDown += OnKey;
            _box.LostKeyboardFocus += (_, _) => { if (!_closing && IsOpen) Close(commit: true); };
            host.Children.Add(_box);
            controller.TextEditRequested += Show;
        }

        public bool IsOpen => _id != null;
        internal TextBox Box => _box;

        private void Show(MarkupItem item, bool isNew)
        {
            Close(commit: true); // a text still open is finished first
            _id = item.Id;
            _closing = false;
            var style = item.Style;
            _box.FontFamily = new FontFamily(style.Font);
            _box.FontSize = style.FontSize;
            _box.FontWeight = style.Bold ? FontWeights.Bold : FontWeights.Normal;
            _box.FontStyle = style.Italic ? FontStyles.Italic : FontStyles.Normal;
            _box.TextDecorations = style.Underline ? System.Windows.TextDecorations.Underline : null;
            _box.Foreground = MarkupRenderer.BrushFor(style.Color);
            _box.CaretBrush = _box.Foreground;
            _box.Text = isNew ? "" : item.Text;
            _box.MinWidth = Math.Max(40, style.FontSize * 3);
            Canvas.SetLeft(_box, item.X1 - 1);
            Canvas.SetTop(_box, item.Y1 - 1);
            _box.Visibility = Visibility.Visible;
            _box.UpdateLayout();
            _box.Focus();
            Keyboard.Focus(_box);
            _box.SelectAll();
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { Close(commit: true); e.Handled = true; }
            else if (e.Key == Key.Escape) { Close(commit: false); e.Handled = true; }
        }

        /// <summary>Closes the box: <paramref name="commit"/> keeps the text, otherwise it is dropped.</summary>
        public void Close(bool commit)
        {
            if (_id == null) return;
            string id = _id;
            _id = null;
            _closing = true;
            string text = _box.Text;
            _box.Visibility = Visibility.Collapsed;
            if (commit) _controller.CommitText(id, text); else _controller.CancelText();
            _closing = false;
        }
    }
}
