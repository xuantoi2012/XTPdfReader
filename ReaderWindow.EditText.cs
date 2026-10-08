using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using XTPdfMergeApp.Services;
using XTPdfMergeApp.Services.TextEdit;
using XTStyle.Controls;
using PageRow = XTPdfMergeApp.Domain.PagePlacement;

namespace XTPdfMergeApp
{
    /// <summary>
    /// Edit Text: click a piece of text on a page that has a real text layer, type the new text, Enter. The edit waits in memory (drawn at once, Undo / Redo,
    /// the unsaved mark) and Ctrl+S removes the old characters from the page and writes the new ones. Scans and drawings whose letters are strokes have no text to edit.
    /// </summary>
    public partial class ReaderWindow
    {
        private sealed record TextEditSession(TextBox Box, PageRow Row, TextRun Original, PageTextRuns Page, string StartText);

        private TextEditSession? _textEditor;
        private bool _textEditBusy;

        private void ReaderEditTextTool_Click(object sender, RoutedEventArgs e) => ToggleReaderTool(ReaderTool.EditText);

        /// <summary>The run on the page with the pending edit of its place put in (what the user sees).</summary>
        private static TextRun Effective(string path, PageTextRuns page, TextRun run)
        {
            var edit = TextEditPendingStore.Page(path, page.PageNumber).FirstOrDefault(e => e.Original.SamePlace(run));
            return edit == null ? run : run with { Text = edit.NewText };
        }

        private async Task BeginTextEditAsync(PageHit hit)
        {
            if (_textEditBusy) return;
            CommitTextEdit();
            if (!TextEditService.IsAvailable)
            {
                XTGrowl.Info("Text editing is not installed with this copy of the program.", this);
                return;
            }
            _textEditBusy = true;
            try
            {
                PageTextRuns? page;
                try { page = await TextEditService.GetRunsAsync(hit.Row.SourcePath, hit.Row.PageNumber); }
                catch (Exception ex) { XTGrowl.Info("The text of this page could not be read: " + ex.Message, this); return; }
                if (page == null || _readerTool != ReaderTool.EditText) return;
                if (page.Runs.Count == 0)
                {
                    XTGrowl.Info("This page has no text layer (a scan, or letters drawn as strokes), so its text cannot be edited. Use Edit Object to select, copy or delete what is drawn.", this);
                    return;
                }
                if (page.Rotation != 0)
                {
                    XTGrowl.Info("Text editing works on upright pages for now. Rotate the page back (Turn left / right), edit, then rotate again.", this);
                    return;
                }
                double x = hit.U * page.Width, y = hit.V * page.Height;
                var run = page.Runs.LastOrDefault(r => r.Contains(x, y));
                if (run == null)
                {
                    XTGrowl.Info("Click on a piece of text.", this);
                    return;
                }
                OpenTextEditor(hit.Row, page, run);
            }
            finally { _textEditBusy = false; }
        }

        private void OpenTextEditor(PageRow row, PageTextRuns page, TextRun original)
        {
            var shown = Effective(row.SourcePath, page, original);
            if (!TryPageToLayer(row, original.X0 / page.Width, original.Y0 / page.Height, out Point topLeft) ||
                !TryPageToLayer(row, original.X1 / page.Width, original.Y1 / page.Height, out Point bottomRight)) return;
            double scale = Math.Max(0.1, (bottomRight.Y - topLeft.Y) / Math.Max(1, original.Y1 - original.Y0));
            var box = new TextBox
            {
                Text = shown.Text,
                FontFamily = new FontFamily("Arial"),
                FontSize = Math.Max(6, original.Size * scale),
                FontWeight = (original.Flags & 16) != 0 ? FontWeights.Bold : FontWeights.Normal,
                FontStyle = (original.Flags & 2) != 0 ? FontStyles.Italic : FontStyles.Normal,
                Foreground = new SolidColorBrush(Color.FromRgb((byte)(original.Color >> 16), (byte)(original.Color >> 8), (byte)original.Color)),
                Background = new SolidColorBrush(Color.FromRgb((byte)(original.Background >> 16), (byte)(original.Background >> 8), (byte)original.Background)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)),
                BorderThickness = new Thickness(1.5),
                Padding = new Thickness(1, 0, 1, 0),
                MinWidth = Math.Max(30, bottomRight.X - topLeft.X + 6),
                AcceptsReturn = false,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            Canvas.SetLeft(box, topLeft.X - 2);
            Canvas.SetTop(box, topLeft.Y - 1);
            box.Height = Math.Max(14, bottomRight.Y - topLeft.Y + 2);
            box.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; CommitTextEdit(); }
                else if (e.Key == Key.Escape) { e.Handled = true; CancelTextEdit(); }
            };
            box.LostKeyboardFocus += (_, e) => { if (!box.IsKeyboardFocusWithin) CommitTextEdit(); };
            _textEditor = new TextEditSession(box, row, original, page, shown.Text);
            ReaderInteractionLayer.Children.Add(box);
            box.Focus();
            Keyboard.Focus(box);
            box.SelectAll();
        }

        private void CancelTextEdit()
        {
            if (_textEditor is not { } session) return;
            _textEditor = null;
            ReaderInteractionLayer.Children.Remove(session.Box);
        }

        /// <summary>Takes the typed text: the edit waits in memory, the page shows it at once.</summary>
        internal void CommitTextEdit()
        {
            if (_textEditor is not { } session) return;
            _textEditor = null;
            ReaderInteractionLayer.Children.Remove(session.Box);
            string text = session.Box.Text.Replace("\r", "").Replace("\n", " ");
            if (text == session.StartText || EditHost == null) return;
            var page = session.Page;
            var edit = text == session.Original.Text ? null : new TextEdit(page.PageNumber, page.Width, page.Height, session.Original, text);
            string name = session.Original.Text.Length > 24 ? session.Original.Text[..24] + "…" : session.Original.Text;
            _ = EditHost.ApplyTextEditAsync(session.Row.SourcePath, page.PageNumber, session.Original, edit, edit == null ? $"Text \"{name}\" restored" : $"Text \"{name}\" edited");
        }
    }
}
