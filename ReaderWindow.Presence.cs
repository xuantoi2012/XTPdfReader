using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using XTPdfMergeApp.Services;

namespace XTPdfMergeApp;

public partial class ReaderWindow
{
    // ── "Ai khác đang mở file này" (tuỳ chọn trong Settings, xem XTPresence) ──

    private readonly HashSet<string> _presencePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _presenceReported = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? _presenceTimer;
    private bool _presenceBusy;
    private void UpdatePresenceStatus(string path)
    {
        string names = AppSettings.ShowPresence && _presenceReported.TryGetValue(path, out var people) ? people : "";
        _presenceOthers = names.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        ReaderPresenceButton.Visibility = _presenceOthers.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ReaderPresenceButton.Content = _presenceOthers.Count.ToString();
        ReaderPresenceButton.ToolTip = _presenceOthers.Count == 0 ? null : Loc.T("Also open: ") + string.Join(", ", _presenceOthers);
        if (_presenceOthers.Count == 0) ReaderPresencePopup.IsOpen = false;
    }

    private List<string> _presenceOthers = new();

    /// <summary>The people icon of the status bar: who else has this file open, one person to a line.</summary>
    private void ReaderPresence_Click(object sender, RoutedEventArgs e)
    {
        ReaderPresenceList.Children.Clear();
        foreach (string name in _presenceOthers)
        {
            var line = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            var icon = new System.Windows.Shapes.Path { Data = (System.Windows.Media.Geometry)FindResource("Ui.Icon.user"), Width = 14, Height = 14, Stretch = System.Windows.Media.Stretch.Uniform, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Ui.Muted");
            var text = new System.Windows.Controls.TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
            text.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Ui.Text");
            line.Children.Add(icon);
            line.Children.Add(text);
            ReaderPresenceList.Children.Add(line);
        }
        Loc.ApplyTree(ReaderPresencePopup);
        ReaderPresencePopup.IsOpen = _presenceOthers.Count > 0 && !ReaderPresencePopup.IsOpen;
    }

    private void ReaderCrosshair_Click(object sender, RoutedEventArgs e) => ToggleCrosshair();

    private void InitializePresence()
    {
        _presenceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _presenceTimer.Tick += (_, _) => _ = PresenceTickAsync();
        _presenceTimer.Start();
        Loaded += (_, _) => Dispatcher.InvokeAsync(async () => { await Task.Delay(1500); await PresenceTickAsync(); });
        Closed += (_, _) =>
        {
            _presenceTimer?.Stop();
            foreach (string path in _presencePaths.ToList()) XTPresence.Remove(path);
        };
    }

    /// <summary>Làm mới dấu "đang mở" của mọi file đang mở và báo (1 lần mỗi lần đổi) nếu người khác cũng đang mở file của tab hiện tại.</summary>
    private async Task PresenceTickAsync()
    {
        if (_presenceBusy) return;
        _presenceBusy = true;
        string? requestedPath = _readerPage?.SourcePath;
        try
        {
            var open = _groups.SelectMany(g => g.Pages.Select(p => p.SourcePath)).Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).ToList();
            string? current = _readerGroup?.Pages.Select(p => p.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ? _readerGroup.Pages[0].SourcePath : null;

            if (!AppSettings.ShowPresence)
            {
                var stale = _presencePaths.ToList();
                _presencePaths.Clear();
                ReaderPresenceButton.Visibility = Visibility.Collapsed;
                if (stale.Count > 0) await Task.Run(() => { foreach (string path in stale) XTPresence.Remove(path); });
                return;
            }

            var toRemove = _presencePaths.Where(p => !open.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
            foreach (string path in open) _presencePaths.Add(path);
            foreach (string path in toRemove) _presencePaths.Remove(path);
            var others = await Task.Run(() =>
            {
                foreach (string path in toRemove) XTPresence.Remove(path);
                foreach (string path in open) XTPresence.Touch(path);
                return current == null ? Array.Empty<string>() : XTPresence.Others(current);
            });

            if (current == null) return;
            string key = string.Join(", ", others);
            string? activePath = _readerPage?.SourcePath;
            _presenceReported.TryGetValue(current, out var previous);
            _presenceReported[current] = key;
            if (string.Equals(activePath, current, StringComparison.OrdinalIgnoreCase)) UpdatePresenceStatus(current);
            if (previous == key) return;
            if (others.Count > 0)
                XTPdfMergeApp.Services.Growl.Warning($"{string.Join(", ", others)} {(others.Count == 1 ? "has" : "have")} \"{Path.GetFileName(current)}\" open too.", this);
        }
        catch { /* thư mục mạng ngắt: bỏ lượt này */ }
        finally
        {
            _presenceBusy = false;
            if (!string.Equals(requestedPath, _readerPage?.SourcePath, StringComparison.OrdinalIgnoreCase)) _ = PresenceTickAsync();
        }
    }
}
