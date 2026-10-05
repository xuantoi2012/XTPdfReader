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
        try
        {
            var open = _groups.SelectMany(g => g.Pages.Select(p => p.SourcePath)).Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).ToList();
            string? current = _readerGroup?.Pages.Select(p => p.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ? _readerGroup.Pages[0].SourcePath : null;

            if (!AppSettings.ShowPresence)
            {
                var stale = _presencePaths.ToList();
                _presencePaths.Clear();
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
            if (_presenceReported.TryGetValue(current, out var last) && last == key) return;
            _presenceReported[current] = key;
            if (others.Count > 0)
                XTStyle.Controls.XTGrowl.Warning($"{string.Join(", ", others)} {(others.Count == 1 ? "has" : "have")} \"{Path.GetFileName(current)}\" open too. Saves are added one after the other.", this);
        }
        catch { /* thư mục mạng ngắt: bỏ lượt này */ }
        finally { _presenceBusy = false; }
    }
}
