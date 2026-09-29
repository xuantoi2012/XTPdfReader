using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using XTStyle.Controls;
using XTPdfMergeApp.Services;

namespace XTPdfMergeApp.Controls;

/// <summary>Tool dialog: chọn phạm vi OCR, tạo bản PDF mới và hiển thị tiến độ/cancel.</summary>
internal sealed class PdfOcrWindow : XTWindow
{
    private readonly string _sourcePath;
    private readonly int _pageCount;
    private readonly int _currentPage;
    private readonly ComboBox _scope = new() { Height = 30, MinWidth = 180 };
    private readonly ComboBox _language = new() { Height = 30, MinWidth = 180 };
    private readonly TextBox _range = new() { Height = 30, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox _output = new() { Height = 30, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _rangeHint = new() { Margin = new Thickness(0, 5, 0, 0), FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 14, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Height = 7, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button _run = new() { Content = "Create searchable copy", MinWidth = 156, Height = 32, IsDefault = true };
    private readonly Button _cancel = new() { Content = "Cancel", Width = 84, Height = 32, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
    private readonly List<Control> _editable = new();
    private CancellationTokenSource? _runCancellation;
    private bool _running;

    public string? OutputPath { get; private set; }

    public PdfOcrWindow(string sourcePath, int pageCount, int currentPage)
    {
        _sourcePath = sourcePath;
        _pageCount = Math.Max(1, pageCount);
        _currentPage = Math.Clamp(currentPage, 1, _pageCount);
        Title = "Make searchable (OCR)";
        TitleBarMode = TitleBarMode.Tool;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        Background = (Brush)Application.Current.FindResource("Ui.Bg");
        Closing += OnClosing;

        _scope.Items.Add("All pages");
        _scope.Items.Add("Current page");
        _scope.Items.Add("Page range");
        _scope.SelectedIndex = 0;
        _scope.SelectionChanged += (_, _) => UpdateScope();
        _range.Text = $"1-{_pageCount}";
        _output.Text = PdfOcrService.DefaultOutputPath(sourcePath);

        foreach (OcrLanguageInfo language in PdfOcrService.GetLanguages())
            _language.Items.Add(language);
        _language.DisplayMemberPath = nameof(OcrLanguageInfo.DisplayName);
        _language.SelectedValuePath = nameof(OcrLanguageInfo.Tag);
        _language.SelectedIndex = _language.Items.Cast<OcrLanguageInfo>().ToList().FindIndex(item => item.Tag == "vi-VN" && item.IsInstalled);
        if (_language.SelectedIndex < 0) _language.SelectedIndex = _language.Items.Cast<OcrLanguageInfo>().ToList().FindIndex(item => item.IsInstalled);

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 24) };
        panel.Children.Add(new TextBlock { Text = "Create a searchable PDF", FontSize = 18, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = Path.GetFileName(sourcePath), Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)Application.Current.FindResource("Ui.Muted")
        });
        panel.Children.Add(new TextBlock
        {
            Text = "OCR runs locally through Windows. Pages that already contain selectable text are left unchanged. The source file is never modified.",
            Margin = new Thickness(0, 14, 0, 0), TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.FindResource("Ui.Muted")
        });

        panel.Children.Add(Labeled("Pages", _scope));
        panel.Children.Add(Labeled("OCR language", _language));
        var rangePanel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        rangePanel.Children.Add(new TextBlock { Text = "Range" });
        rangePanel.Children.Add(_range);
        rangePanel.Children.Add(_rangeHint);
        panel.Children.Add(rangePanel);
        panel.Children.Add(OutputRow());
        panel.Children.Add(_status);
        panel.Children.Add(_progress);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(_run);
        buttons.Children.Add(_cancel);
        panel.Children.Add(buttons);
        Content = panel;

        _editable.AddRange(new Control[] { _scope, _language, _range, _output });
        _run.Click += Run_Click;
        _cancel.Click += Cancel_Click;
        _language.SelectionChanged += (_, _) => UpdateLanguageAvailability();
        UpdateScope();
        UpdateLanguageAvailability();
    }

    private UIElement Labeled(string label, Control control)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 5) });
        panel.Children.Add(control);
        return panel;
    }

    private UIElement OutputRow()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "Save searchable copy as", Margin = new Thickness(0, 0, 0, 5) });
        var row = new DockPanel();
        var browse = new Button { Content = "Browse…", Width = 84, Height = 30, Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += Browse_Click;
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(browse);
        row.Children.Add(_output);
        panel.Children.Add(row);
        _editable.Add(browse);
        return panel;
    }

    private void UpdateScope()
    {
        bool custom = _scope.SelectedIndex == 2;
        _range.IsEnabled = custom;
        _rangeHint.Text = custom ? $"Example: 1-3, 6, 10-12 (1 to {_pageCount})"
            : _scope.SelectedIndex == 1 ? $"Current source page: {_currentPage}" : $"All {_pageCount} pages in this PDF";
    }

    private void UpdateLanguageAvailability()
    {
        bool hasLanguage = _language.SelectedItem is OcrLanguageInfo { IsInstalled: true };
        _run.IsEnabled = hasLanguage;
        _status.Text = hasLanguage ? "" : "No supported OCR language is installed. Add Vietnamese or English OCR in Windows Language settings, then reopen this dialog.";
        _status.Foreground = hasLanguage ? (Brush)Application.Current.FindResource("Ui.Muted") : Brushes.IndianRed;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.SaveFileDialog
        {
            Title = "Save searchable PDF copy",
            Filter = "PDF files (*.pdf)|*.pdf",
            FileName = Path.GetFileName(_output.Text),
            InitialDirectory = Path.GetDirectoryName(_output.Text)
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) _output.Text = dialog.FileName;
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_running || _language.SelectedItem is not OcrLanguageInfo { IsInstalled: true } language) return;
        IReadOnlyList<int> pages;
        try { pages = SelectedPages(); }
        catch (Exception ex) { _status.Text = ex.Message; _status.Foreground = Brushes.IndianRed; return; }
        string output = _output.Text.Trim();
        if (output.Length == 0) { _status.Text = "Choose an output file."; _status.Foreground = Brushes.IndianRed; return; }
        if (File.Exists(output) && MessageBox.Show(this, $"\"{Path.GetFileName(output)}\" already exists. Replace it after OCR succeeds?", "Replace searchable copy", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _running = true;
        _run.Content = "Working…";
        _cancel.Content = "Cancel";
        foreach (Control control in _editable) control.IsEnabled = false;
        _progress.Visibility = Visibility.Visible;
        _progress.Maximum = pages.Count;
        _progress.Value = 0;
        _status.Foreground = (Brush)Application.Current.FindResource("Ui.Muted");
        _status.Text = "Preparing OCR…";
        _runCancellation = new CancellationTokenSource();
        try
        {
            var report = new Progress<PdfOcrProgress>(value =>
            {
                _progress.Maximum = Math.Max(1, value.TotalPages);
                _progress.Value = value.CompletedPages;
                _status.Text = value.Message;
            });
            var result = await PdfOcrService.CreateSearchableCopyAsync(new PdfOcrOptions(_sourcePath, output, pages, language.Tag), report, _runCancellation.Token);
            OutputPath = result.OutputPath;
            _running = false;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            _status.Text = "OCR cancelled. No output PDF was created.";
            _status.Foreground = (Brush)Application.Current.FindResource("Ui.Muted");
            RestoreConfiguration();
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            _status.Foreground = Brushes.IndianRed;
            RestoreConfiguration();
        }
        finally
        {
            _runCancellation?.Dispose();
            _runCancellation = null;
        }
    }

    private IReadOnlyList<int> SelectedPages()
    {
        if (_scope.SelectedIndex == 0) return Enumerable.Range(1, _pageCount).ToArray();
        if (_scope.SelectedIndex == 1) return new[] { _currentPage };
        var pages = new SortedSet<int>();
        foreach (string item in _range.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = item.Split('-', StringSplitOptions.TrimEntries);
            if (!int.TryParse(parts[0], out int start)) throw new InvalidOperationException("Use page numbers such as 1-3, 6, 10-12.");
            int end = start;
            if (parts.Length == 2 && !int.TryParse(parts[1], out end)) throw new InvalidOperationException("Use page numbers such as 1-3, 6, 10-12.");
            if (parts.Length > 2 || start < 1 || end < start || end > _pageCount) throw new InvalidOperationException($"Pages must be between 1 and {_pageCount}.");
            for (int page = start; page <= end; page++) pages.Add(page);
        }
        if (pages.Count == 0) throw new InvalidOperationException("Choose at least one page.");
        return pages.ToArray();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_running) { _runCancellation?.Cancel(); return; }
        DialogResult = false;
    }

    private void RestoreConfiguration()
    {
        _running = false;
        _run.Content = "Create searchable copy";
        foreach (Control control in _editable) control.IsEnabled = true;
        _progress.Visibility = Visibility.Collapsed;
        UpdateLanguageAvailability();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_running) return;
        _runCancellation?.Cancel();
        e.Cancel = true;
    }
}
