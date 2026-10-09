using System;
using XTPdfMergeApp.Services;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using XTPdfMergeApp.Services.Ocr;
using XTStyle.Controls;

namespace XTPdfMergeApp.Controls;

/// <summary>
/// "OCR": reads the text of scanned pages (Vietnamese and Latin letters). By default the text is KEPT IN THIS FILE'S OPEN TAB until the user saves (Ctrl+S): Find,
/// Select and the sheet-info reader use it at once and Undo takes it away (<see cref="PendingWords"/>). The other choice writes a COPY "name (OCR).pdf" at once
/// (<see cref="OutputPath"/>). The original file is never changed by this dialog.
/// </summary>
internal sealed class OcrWindow : XTWindow
{
    private readonly string _path;
    private readonly int _pageCount;
    private readonly RadioButton _thisPage, _allPages, _range;
    private readonly TextBox _rangeBox = new() { Width = 150, Height = 26, Margin = new Thickness(8, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "For example 1-3, 7" };
    private readonly CheckBox _skip = new() { Content = "Skip pages that already have text", IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
    private readonly RadioButton _keepPending = new() { Content = "Keep the text in this file until I save (Ctrl+S)", GroupName = "OcrWhere", IsChecked = true, Margin = new Thickness(0, 12, 0, 2) };
    private readonly RadioButton _asCopy = new() { Content = "Save a searchable copy now (name (OCR).pdf)", GroupName = "OcrWhere", Margin = new Thickness(0, 2, 0, 0) };
    private readonly ProgressBar _progress = new() { Height = 8, Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed, Minimum = 0, Maximum = 1 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _start = new() { Content = "Start", Width = 90, Height = 28, IsDefault = true };
    private readonly Button _close = new() { Content = "Close", Width = 90, Height = 28, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
    private CancellationTokenSource? _cancel;
    private bool _running;

    public string? OutputPath { get; private set; }

    /// <summary>The words by page number (1 based) when the choice was "keep until I save"; the caller puts them into the open file's pending changes.</summary>
    public Dictionary<int, IReadOnlyList<OcrWord>>? PendingWords { get; private set; }

    /// <summary>True for the quick per-page command: the dialog starts reading at once and closes when it is done.</summary>
    public bool AutoStart { get; init; }
    internal RadioButton KeepPendingButton => _keepPending;
    internal RadioButton CopyButton => _asCopy;

    internal RadioButton AllPagesButton => _allPages;
    internal RadioButton RangeButton => _range;
    internal TextBox RangeBox => _rangeBox;
    internal CheckBox SkipBox => _skip;
    internal Button StartButton => _start;
    internal string StatusText => _status.Text;

    public OcrWindow(string path, int currentPage, int pageCount)
    {
        _path = path;
        _pageCount = Math.Max(1, pageCount);
        Title = "OCR: read scanned pages";
        TitleBarMode = TitleBarMode.Dialog;
        Width = 500;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        Brush R(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

        var panel = new StackPanel { Margin = new Thickness(22, 18, 22, 20) };
        panel.Children.Add(new TextBlock
        {
            Text = "Reads the text of scanned pages (Vietnamese and Latin letters) so that Find, Select, Copy and Read sheet info work on them. The text is added invisibly, on top of the picture.",
            TextWrapping = TextWrapping.Wrap, Foreground = R("Ui.Text")
        });
        panel.Children.Add(new TextBlock { Text = System.IO.Path.GetFileName(path), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 6), TextTrimming = TextTrimming.CharacterEllipsis, Foreground = R("Ui.Text") });
        _thisPage = new RadioButton { Content = $"This page ({currentPage})", GroupName = "OcrPages", Margin = new Thickness(0, 2, 0, 2) };
        _allPages = new RadioButton { Content = $"All {_pageCount} pages", GroupName = "OcrPages", IsChecked = true, Margin = new Thickness(0, 2, 0, 2) };
        _range = new RadioButton { Content = "Pages", GroupName = "OcrPages", VerticalAlignment = VerticalAlignment.Center };
        var rangeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0), Children = { _range, _rangeBox } };
        _rangeBox.GotFocus += (_, _) => _range.IsChecked = true;
        _currentPage = currentPage;
        panel.Children.Add(_thisPage);
        panel.Children.Add(_allPages);
        panel.Children.Add(rangeRow);
        panel.Children.Add(_skip);
        panel.Children.Add(_keepPending);
        panel.Children.Add(_asCopy);
        panel.Children.Add(_progress);
        panel.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { _start, _close } };
        panel.Children.Add(buttons);
        Content = new Border { Background = R("Ui.Surface"), Child = panel };

        if (!OcrService.IsAvailable())
        {
            _start.IsEnabled = false;
            _status.Text = Loc.T("OCR is not installed with this copy of the program.");
        }
        _start.Click += async (_, _) => await StartAsync();
        Loaded += async (_, _) => { if (AutoStart && _start.IsEnabled) await StartAsync(); };
        _close.Click += (_, _) => { if (_running) _cancel?.Cancel(); };
        Closing += (_, _) => _cancel?.Cancel();
    }

    private readonly int _currentPage;

    /// <summary>"1-3, 7" as zero based page numbers, in order, without repeats; null when the text is not a valid list for a document of <paramref name="pageCount"/> pages.</summary>
    /// <summary>Chooses exactly these pages (1 based) before the dialog is shown.</summary>
    internal void SelectPages(IReadOnlyList<int> pages)
    {
        _rangeBox.Text = string.Join(", ", pages);
        _range.IsChecked = true;
    }

    internal static List<int>? ParsePages(string text, int pageCount)
    {
        var pages = new SortedSet<int>();
        foreach (string part in text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ends = part.Split('-', StringSplitOptions.TrimEntries);
            if (ends.Length is < 1 or > 2 || !int.TryParse(ends[0], out int from) || !int.TryParse(ends[^1], out int to)) return null;
            if (from < 1 || to < from || to > pageCount) return null;
            for (int p = from; p <= to; p++) pages.Add(p - 1);
        }
        return pages.Count == 0 ? null : pages.ToList();
    }

    private List<int>? SelectedPages()
    {
        if (_thisPage.IsChecked == true) return new List<int> { _currentPage - 1 };
        if (_range.IsChecked == true) return ParsePages(_rangeBox.Text, _pageCount);
        return Enumerable.Range(0, _pageCount).ToList();
    }

    internal async Task StartAsync()
    {
        if (_running) return;
        var pages = SelectedPages();
        if (pages == null) { _status.Text = $"Type the pages like 1-3, 7 (this file has {_pageCount})."; _rangeBox.Focus(); return; }
        _running = true;
        _start.IsEnabled = false;
        _close.Content = Loc.T("Cancel");
        _progress.Visibility = Visibility.Visible;
        _progress.Value = 0;
        _status.Text = "Reading page 1 of " + pages.Count + "…";
        _cancel = new CancellationTokenSource();
        bool keep = _keepPending.IsChecked == true;
        if (_skip.IsChecked == true) pages = pages.Where(p => !OcrPendingStore.HasPage(_path, p + 1)).ToList();
        if (pages.Count == 0) { _status.Text = "Those pages already have OCR text waiting to be saved."; _running = false; _start.IsEnabled = true; _close.Content = "Close"; _progress.Visibility = Visibility.Collapsed; return; }
        string output = PdfOcrWriter.OutputPathFor(_path);
        try
        {
            var progress = new Progress<(int Done, int Total)>(p =>
            {
                _progress.Maximum = Math.Max(1, p.Total);
                _progress.Value = p.Done;
                _status.Text = p.Done >= p.Total ? "Writing the text into the copy…" : $"Reading page {p.Done + 1} of {p.Total}…";
            });
            var results = await OcrService.RunAsync(_path, pages, new OcrOptions(SkipPagesWithText: _skip.IsChecked == true), progress, _cancel.Token);
            var failed = results.Where(r => r.Error != null).ToList();
            int words = keep ? results.Where(r => !r.Skipped).Sum(r => r.Words.Count) : await Task.Run(() => PdfOcrWriter.Write(_path, output, results), _cancel.Token);
            int read = results.Count(r => !r.Skipped && r.Words.Count > 0), skipped = results.Count(r => r.Skipped);
            if (keep && words > 0)
            {
                PendingWords = results.Where(r => !r.Skipped && r.Words.Count > 0).ToDictionary(r => r.PageIndex + 1, r => r.Words);
                _status.Text = $"{words} words read on {read} page{(read == 1 ? "" : "s")}" + (failed.Count > 0 ? $", {failed.Count} page{(failed.Count == 1 ? "" : "s")} could not be read" : "") + ". Ctrl+S saves them into the file.";
                try { DialogResult = true; } catch (InvalidOperationException) { /* shown without ShowDialog (tests) */ }
                return;
            }
            if (words == 0)
            {
                if (!keep) try { File.Delete(output); } catch { /* nothing was written to keep */ }
                _status.Text = skipped == results.Count ? "Every page of the choice already has text: nothing to read." : "No text was found on those pages.";
            }
            else
            {
                OutputPath = output;
                _status.Text = $"{words} words read on {read} page{(read == 1 ? "" : "s")}" + (skipped > 0 ? $", {skipped} page{(skipped == 1 ? "" : "s")} already had text" : "") +
                    (failed.Count > 0 ? $", {failed.Count} page{(failed.Count == 1 ? "" : "s")} could not be read" : "") + "." + Environment.NewLine + "Saved as " + System.IO.Path.GetFileName(output);
                try { DialogResult = true; } catch (InvalidOperationException) { /* shown without ShowDialog (tests): the caller closes it */ }
                return;
            }
        }
        catch (OperationCanceledException)
        {
            try { if (File.Exists(output) && _asCopy.IsChecked == true) File.Delete(output); } catch { /* partial copy */ }
            _status.Text = Loc.T("Cancelled.");
        }
        catch (Exception ex)
        {
            try { if (File.Exists(output) && _asCopy.IsChecked == true) File.Delete(output); } catch { /* partial copy */ }
            _status.Text = "OCR failed: " + ex.Message;
        }
        finally
        {
            _running = false;
            _start.IsEnabled = OcrService.IsAvailable();
            _close.Content = Loc.T("Close");
            _progress.Visibility = Visibility.Collapsed;
        }
    }
}
