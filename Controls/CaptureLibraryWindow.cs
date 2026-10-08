using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XTPdfMergeApp.Services;
using XTPdfMergeApp.Services.Capture;
using XTStyle.Controls;

namespace XTPdfMergeApp.Controls;

/// <summary>
/// The store of screen captures: newest first, a picture of each, and Open (edit in the Reader), Copy, Save PNG and Delete. Every capture is
/// a PDF in <see cref="CaptureLibrary.Folder"/>, so what was added to it (text, notes, shapes) is rendered into the copy and the PNG.
/// </summary>
internal sealed class CaptureLibraryWindow : XTWindow
{
    private static CaptureLibraryWindow? _current;

    private readonly Window _owner;
    private readonly Action<string> _open;
    private readonly Action _newCapture;
    private readonly Func<IEnumerable<string>> _openPaths;
    private readonly WrapPanel _wrap = new() { Margin = new Thickness(14, 8, 14, 14) };
    private readonly TextBlock _emptyText;
    private readonly TextBlock _summary;
    private readonly CancellationTokenSource _closing = new();
    private int _loadVersion;

    internal int CardCount => _wrap.Children.Count;

    private CaptureLibraryWindow(Window owner, Action<string> open, Action newCapture, Func<IEnumerable<string>> openPaths)
    {
        _owner = owner;
        _open = open;
        _newCapture = newCapture;
        _openPaths = openPaths;
        Title = "Captures";
        TitleBarMode = TitleBarMode.Dialog;
        Width = 980;
        Height = 660;
        MinWidth = 560;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = owner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        Closed += (_, _) => { _closing.Cancel(); if (ReferenceEquals(_current, this)) _current = null; };
        Activated += (_, _) => Reload();

        Brush R(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
        var root = new DockPanel { Background = R("Ui.Surface") };

        var header = new DockPanel { Margin = new Thickness(18, 12, 18, 4), LastChildFill = false };
        _summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = R("Ui.Muted") };
        DockPanel.SetDock(_summary, Dock.Left);
        header.Children.Add(_summary);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right);
        buttons.Children.Add(MakeButton("New capture", "Capture the screen now", () => { Hide(); _newCapture(); }, accent: true));
        buttons.Children.Add(MakeButton("Clean up…", "Remove the captures older than a number of days", CleanUp));
        buttons.Children.Add(MakeButton("Open folder", "Show the folder in Explorer", OpenFolder));
        header.Children.Add(buttons);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        _emptyText = new TextBlock
        {
            Text = "Nothing captured yet. Use New capture, then Copy or Edit: every capture is kept here so you can open it and add text or comments later.",
            Margin = new Thickness(22, 20, 22, 0), TextWrapping = TextWrapping.Wrap, Foreground = R("Ui.Muted"), Visibility = Visibility.Collapsed
        };
        DockPanel.SetDock(_emptyText, Dock.Top);
        root.Children.Add(_emptyText);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _wrap });
        Content = root;
        Reload();
    }

    /// <summary>Opens the window (one at a time); <paramref name="open"/> opens a capture in the Reader, <paramref name="newCapture"/> starts a capture.</summary>
    internal static CaptureLibraryWindow ShowFor(Window owner, Action<string> open, Action newCapture, Func<IEnumerable<string>> openPaths)
    {
        if (_current != null)
        {
            _current.Show();
            _current.Activate();
            return _current;
        }
        _current = new CaptureLibraryWindow(owner, open, newCapture, openPaths);
        _current.Show();
        return _current;
    }

    private Border MakeButton(string text, string tip, Action click, bool accent = false)
    {
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Foreground = accent ? Brushes.White : (TryFindResource("Ui.Text") as Brush ?? Brushes.Black) };
        var border = new Border
        {
            Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(6, 0, 0, 0), CornerRadius = new CornerRadius(5), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = tip, Child = label,
            Background = accent ? new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)) : Brushes.Transparent,
            BorderThickness = new Thickness(1), BorderBrush = accent ? Brushes.Transparent : (TryFindResource("Ui.Border") as Brush ?? Brushes.LightGray)
        };
        border.MouseLeftButtonUp += (_, _) => click();
        return border;
    }

    // ── List ─────────────────────────────────────────────────────────

    internal void Reload()
    {
        int version = ++_loadVersion;
        var entries = CaptureLibrary.List();
        // the cards already shown keep their picture; only a changed list rebuilds them
        var shown = _wrap.Children.OfType<Border>().Select(c => c.Tag as string).ToList();
        if (shown.SequenceEqual(entries.Select(e => e.Path + "|" + File.GetLastWriteTimeUtc(e.Path).Ticks))) { UpdateSummary(entries); return; }
        _wrap.Children.Clear();
        foreach (var entry in entries) _wrap.Children.Add(MakeCard(entry, version));
        UpdateSummary(entries);
    }

    private void UpdateSummary(List<CaptureLibrary.Entry> entries)
    {
        _summary.Text = entries.Count == 0 ? "" : $"{entries.Count} capture{(entries.Count == 1 ? "" : "s")}, {PrintedFilesService.FormatSize(entries.Sum(e => e.Length))}";
        _emptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Border MakeCard(CaptureLibrary.Entry entry, int version)
    {
        Brush R(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
        var picture = new Image { Stretch = Stretch.Uniform, Height = 130, Margin = new Thickness(0, 0, 0, 6) };
        var well = new Border { Background = R("Ui.Hover"), CornerRadius = new CornerRadius(5), Padding = new Thickness(4), Child = picture, Cursor = System.Windows.Input.Cursors.Hand };
        well.MouseLeftButtonUp += (_, _) => _open(entry.Path);

        var name = new TextBlock { Text = entry.Created.ToString("dd MMM yyyy  HH:mm:ss"), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = R("Ui.Text") };
        var meta = new TextBlock { Text = PrintedFilesService.FormatSize(entry.Length), FontSize = 11.5, Foreground = R("Ui.Muted"), Margin = new Thickness(0, 1, 0, 6) };
        var actions = new WrapPanel();
        actions.Children.Add(LinkButton("Open", "Open to add text and comments", () => _open(entry.Path)));
        actions.Children.Add(LinkButton("Copy", "Copy the picture (with what was added)", () => _ = CopyAsync(entry.Path)));
        actions.Children.Add(LinkButton("Save PNG", "Save the picture as a PNG file", () => _ = SavePngAsync(entry.Path)));
        actions.Children.Add(LinkButton("Delete", "Move to the Recycle Bin", () => Delete(entry)));

        var stack = new StackPanel();
        stack.Children.Add(well);
        stack.Children.Add(name);
        stack.Children.Add(meta);
        stack.Children.Add(actions);
        var card = new Border
        {
            Width = 224, Margin = new Thickness(0, 0, 12, 12), Padding = new Thickness(8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            BorderBrush = R("Ui.Border"), Child = stack, ToolTip = entry.Path, Tag = entry.Path + "|" + File.GetLastWriteTimeUtc(entry.Path).Ticks
        };
        _ = LoadThumbnailAsync(entry.Path, picture, version);
        return card;
    }

    private TextBlock LinkButton(string text, string tip, Action click)
    {
        var link = new TextBlock { Text = text, Margin = new Thickness(0, 0, 12, 0), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = tip };
        link.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Accent");
        link.MouseLeftButtonUp += (_, _) => click();
        return link;
    }

    private async Task LoadThumbnailAsync(string path, Image target, int version)
    {
        try
        {
            var image = await PdfThumbnailService.RenderPageAsync(path, 0, 420, _closing.Token, withAnnotations: true).ConfigureAwait(true);
            if (image != null && version == _loadVersion) target.Source = image;
        }
        catch { /* the thumbnail stays empty */ }
    }

    // ── Actions ──────────────────────────────────────────────────────

    /// <summary>The capture as a picture: its page rendered with what was added, at the size it was taken.</summary>
    internal static async Task<BitmapSource?> RenderAsync(string path)
    {
        double width = CaptureLibrary.PixelWidth(path);
        return await PdfThumbnailService.RenderPageAsync(path, 0, Math.Clamp(width, 16, 8000), withAnnotations: true).ConfigureAwait(true);
    }

    private async Task CopyAsync(string path)
    {
        var image = await RenderAsync(path);
        if (image == null) { XTGrowl.Info("Could not read that capture.", this); return; }
        CaptureClipboard.Copy(image);
        XTGrowl.Success("Copied", this);
    }

    private async Task SavePngAsync(string path)
    {
        var image = await RenderAsync(path);
        if (image == null) { XTGrowl.Info("Could not read that capture.", this); return; }
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "PNG picture (*.png)|*.png", FileName = Path.GetFileNameWithoutExtension(path) + ".png", AddExtension = true, DefaultExt = ".png" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllBytes(dialog.FileName, CaptureImaging.EncodePng(image));
            XTGrowl.Success("Saved", this);
        }
        catch (Exception ex) { XTGrowl.Info("Could not save the picture: " + ex.Message, this); }
    }

    private void Delete(CaptureLibrary.Entry entry)
    {
        if (_openPaths().Any(p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(entry.Path), StringComparison.OrdinalIgnoreCase)))
        {
            AppDialog.Show(this, "This capture is open in a tab. Close the tab first.", "Captures", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (AppDialog.Show(this, "Move this capture to the Recycle Bin?", "Captures", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        PrintedFilesService.Delete(new[] { new PrintedFilesService.Candidate(entry.Path, entry.Length) });
        Reload();
    }

    private void CleanUp()
    {
        var prompt = new NumberPromptWindow("Clean up captures", "Remove the captures older than this many days:", 1, 3650, AppSettings.CaptureCleanupDays) { Owner = this };
        if (prompt.ShowDialog() != true || prompt.Value is not int days) return;
        AppSettings.CaptureCleanupDays = days;
        var old = CaptureLibrary.FindOlderThan(days, _openPaths());
        if (old.Count == 0) { AppDialog.Show(this, $"No capture is older than {days} days.", "Captures", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        long bytes = old.Sum(c => c.Length);
        if (AppDialog.Show(this, $"Move {old.Count} capture{(old.Count == 1 ? "" : "s")} ({PrintedFilesService.FormatSize(bytes)}) to the Recycle Bin?", "Captures",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        int removed = PrintedFilesService.Delete(old);
        Reload();
        XTGrowl.Success($"{removed} removed", this);
    }

    private static void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(CaptureLibrary.Folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + CaptureLibrary.Folder + "\"") { UseShellExecute = true });
        }
        catch { /* no Explorer: nothing to show */ }
    }
}
