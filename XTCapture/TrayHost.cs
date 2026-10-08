using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;

namespace XTCapture
{
    /// <summary>
    /// What stays running: the tray icon, the shortcut, the corner button, and the capture itself (freeze the screen, pick, keep it in the Store).
    /// <see cref="Execute"/> takes the commands other programs send ("capture", "store", "settings", "exit").
    /// </summary>
    internal sealed class TrayHost : IDisposable
    {
        private readonly bool _showTray;
        private NotifyIcon? _tray;
        private ToolStripMenuItem? _captureItem, _autostartItem;
        private FloatingCaptureButton? _floating;
        private bool _capturing;

        public HotkeyService Hotkey { get; } = new();
        internal bool IsCapturing => _capturing;
        internal FloatingCaptureButton? Floating => _floating;

        /// <summary>Replaces the real overlay in tests (they cannot click on the screen).</summary>
        internal Func<ScreenSnapshot, double, CaptureOverlayWindow>? OverlayFactory { get; set; }

        /// <summary>Raised after Exit was chosen (the application then shuts down).</summary>
        public event Action? ExitRequested;

        public TrayHost(bool showTray = true) => _showTray = showTray;

        public void Start()
        {
            if (_showTray) CreateTray();
            Hotkey.Pressed += () => System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() => _ = CaptureAsync()));
            ApplyHotkey();
            ApplyFloatingButton();
            if (CaptureSettings.FirstRun)
            {
                CaptureSettings.FirstRun = false;
                // The owner wants it to start with Windows; Settings turns that off.
                if (!Autostart.IsEnabled) Autostart.Set(true);
                ToastWindow.Display("XT Capture is running. " + Hotkey.Message + ". It starts with Windows (change this in Settings).");
            }
        }

        // ── Tray ────────────────────────────────────────────────────────

        private void CreateTray()
        {
            Icon? icon = null;
            try { icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? ""); } catch { /* the default application icon */ }
            var menu = new ContextMenuStrip();
            _captureItem = new ToolStripMenuItem("Capture", null, (_, _) => Execute("capture"));
            _autostartItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => { SetAutostart(!Autostart.IsEnabled); });
            menu.Items.Add(_captureItem);
            menu.Items.Add(new ToolStripMenuItem("Store", null, (_, _) => Execute("store")));
            menu.Items.Add(new ToolStripMenuItem("Settings", null, (_, _) => Execute("settings")));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_autostartItem);
            menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => Execute("exit")));
            menu.Opening += (_, _) => { _autostartItem.Checked = Autostart.IsEnabled; _captureItem.Text = "Capture    " + (Hotkey.Active?.ToString() ?? ""); };
            _tray = new NotifyIcon { Icon = icon ?? SystemIcons.Application, Visible = true, Text = "XT Capture", ContextMenuStrip = menu };
            _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Execute("capture"); };
        }

        private void UpdateTrayText()
        {
            if (_tray != null) _tray.Text = "XT Capture" + (Hotkey.Active is { } key ? "  (" + key + ")" : "");
        }

        // ── Commands ────────────────────────────────────────────────────

        public void Execute(string command)
        {
            switch (command)
            {
                case "capture": _ = CaptureAsync(); break;
                case "store": OpenStore(); break;
                case "settings": SettingsWindow.Open(this); break;
                case "exit": Exit(); break;
            }
        }

        public void OpenStore() => StoreWindow.Open(() => _ = CaptureAsync(), () => SettingsWindow.Open(this));

        public void Exit() => ExitRequested?.Invoke();

        // ── Settings that act at once ───────────────────────────────────

        public void ApplyHotkey()
        {
            Hotkey.Apply(CaptureSettings.Hotkey);
            UpdateTrayText();
        }

        public void SetHotkey(string text)
        {
            CaptureSettings.Hotkey = text;
            ApplyHotkey();
        }

        public void SetAutostart(bool enabled) => Autostart.Set(enabled);

        public void SetFloatingButton(bool show)
        {
            CaptureSettings.ShowFloatingButton = show;
            ApplyFloatingButton();
        }

        public void ApplyFloatingButton()
        {
            if (CaptureSettings.ShowFloatingButton)
            {
                if (_floating != null) return;
                _floating = new FloatingCaptureButton();
                _floating.Clicked += () => _ = CaptureAsync();
                _floating.StoreRequested += OpenStore;
                _floating.SettingsRequested += () => SettingsWindow.Open(this);
                _floating.HideRequested += () => SetFloatingButton(false);
                _floating.ExitRequested += Exit;
                _floating.Show();
            }
            else if (_floating != null)
            {
                _floating.Close();
                _floating = null;
            }
        }

        // ── Capture ─────────────────────────────────────────────────────

        public async Task CaptureAsync()
        {
            if (_capturing) return;
            _capturing = true;
            bool storeWasOpen = StoreWindow.IsOpen;
            try
            {
                // Nothing of ours may be in the picture: the corner button and the Store go away for a moment (and the screen repaints) before it is frozen.
                _floating?.Hide();
                StoreWindow.HideCurrent();
                await Task.Delay(230);
                var snapshot = ScreenGrabber.Snapshot();
                var overlay = OverlayFactory?.Invoke(snapshot, ScreenGrabber.SystemScale) ?? new CaptureOverlayWindow(snapshot, ScreenGrabber.SystemScale);
                overlay.ShowDialog();
                if (overlay.Outcome is { } outcome) await HandleAsync(outcome);
            }
            catch (Exception ex)
            {
                ToastWindow.Display("The capture failed: " + ex.Message);
            }
            finally
            {
                _capturing = false;
                _floating?.Show();
                if (storeWasOpen) StoreWindow.Open(() => _ = CaptureAsync(), () => SettingsWindow.Open(this));
            }
        }

        /// <summary>Keeps the picture in the Store; Copy also puts it on the clipboard, Store opens the Store.</summary>
        internal async Task<CaptureStore.Entry?> HandleAsync(CaptureOutcome outcome)
        {
            CaptureStore.Entry entry;
            try { entry = await Task.Run(() => CaptureStore.Save(outcome.Image)); }
            catch (Exception ex)
            {
                ToastWindow.Display("Could not keep the capture: " + ex.Message);
                return null;
            }
            if (outcome.Action == CaptureAction.Copy)
            {
                try
                {
                    CaptureClipboard.Copy(outcome.Image);
                    ToastWindow.Display("Copied. Kept in the Store.");
                }
                catch (Exception ex) { ToastWindow.Display("Kept in the Store, but the clipboard is busy: " + ex.Message); }
            }
            else
            {
                ToastWindow.Display("Kept in the Store.");
                OpenStore();
            }
            return entry;
        }

        public void Dispose()
        {
            Hotkey.Dispose();
            _floating?.Close();
            _floating = null;
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
        }
    }
}
