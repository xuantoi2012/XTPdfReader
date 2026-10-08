using System;
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
/// "OCR": reads the text of scanned pages (Vietnamese and Latin letters) and writes a COPY of the file with an invisible text layer, so Find, Select, Copy and the
/// sheet-info reader work on it. The original is never changed. The copy is <see cref="OutputPath"/> when the dialog ends with true.
/// </summary>
internal sealed class OcrWindow : XTWindow
{
    private readonly string _path;
    private readonly int _pageCount;
    private readonly RadioButton _thisPage, _allPages, _range;
    private readonly TextBox _rangeBox = new() { Width = 150, Height = 26, Margin = new Thickness(8, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "For example 1-3, 7" };
    private readonly CheckBox _skip = new() { Content = "Skip pages that already have text", IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
    private readonly ProgressBar _progress = new() { Height = 8, Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed, Minimum = 0, Maximum = 1 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _start = new() { Content = "Start", Width = 90, Height = 28, IsDefault = true };
    private readonly Button _close = new() { Content = "Close", Width = 90, Height = 28, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
    private CancellationTokenSource? _cancel;
    private bool _running;

    public string? OutputPath { get; private set; }

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
            Text = "Reads the text of scanned pages (Vietnamese and Latin letters) and saves a copy of the file with that text added invisibly, so Find, Select and Copy work. The original file is not changed.",
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
        panel.Children.Add(_progress);
        panel.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { _start, _close } };
        panel.Children.Add(buttons);
        Content = new Border { Background = R("Ui.Surface"), Child = panel };

        if (!OcrService.IsAvailable())
        {
            _start.IsEnabled = false;
            _status.Text = "OCR is not installed with this copy of the program.";
        }
        _start.Click += async (_, _) => await StartAsync();
        _close.Click += (_, _) => { if (_running) _cancel?.Cancel(); };
        Closing += (_, _) => _cancel?.Cancel();
    }

    private readonly int _currentPage;

    /// <summary>"1-3, 7" as zero based page numbers, in order, without repeats; null when the text is not a valid list for a document of <paramref name="pageCount"/> pages.</summary>
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
        _close.Content = "Cancel";
        _progress.Visibility = Visibility.Visible;
        _progress.Value = 0;
        _status.Text = "Reading page 1 of " + pages.Count + "…";
        _cancel = new CancellationTokenSource();
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
            int words = await Task.Run(() => PdfOcrWriter.Write(_path, output, results), _cancel.Token);
            int read = results.Count(r => !r.Skipped && r.Words.Count > 0), skipped = results.Count(r => r.Skipped);
            if (words == 0)
            {
                try { File.Delete(output); } catch { /* nothing was written to keep */ }
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
            try { if (File.Exists(output)) File.Delete(output); } catch { /* partial copy */ }
            _status.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            try { if (File.Exists(output)) File.Delete(output); } catch { /* partial copy */ }
            _status.Text = "OCR failed: " + ex.Message;
        }
        finally
        {
            _running = false;
            _start.IsEnabled = OcrService.IsAvailable();
            _close.Content = "Close";
            _progress.Visibility = Visibility.Collapsed;
        }
    }
}
