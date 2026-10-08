using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace XTCapture
{
    /// <summary>A global shortcut: modifiers + one key, as text ("Win+C", "Ctrl+Alt+F9", "PrintScreen").</summary>
    internal readonly record struct CaptureHotkey(uint Modifiers, uint VirtualKey)
    {
        public const uint Alt = 1, Ctrl = 2, Shift = 4, Win = 8;

        /// <summary>Offered in Settings, most wanted first. Win+Alt+C is the default (chosen by the owner). Win+C belongs to Windows (Copilot) on many PCs, so it often cannot be taken; it stays in the list for PCs where it is free.</summary>
        public static readonly string[] Presets = { "Win+Alt+C", "Ctrl+Alt+C", "Ctrl+Shift+C", "Ctrl+Alt+F9", "PrintScreen", "Win+C" };

        /// <summary>Tried in this order when the chosen shortcut is taken by another program.</summary>
        public static readonly string[] Fallbacks = { "Ctrl+Alt+C", "Ctrl+Shift+C", "Ctrl+Alt+F9", "Win+Alt+C" };

        public static CaptureHotkey? Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            uint modifiers = 0, key = 0;
            foreach (string raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (raw.ToLowerInvariant())
                {
                    case "win": case "windows": modifiers |= Win; continue;
                    case "ctrl": case "control": modifiers |= Ctrl; continue;
                    case "alt": modifiers |= Alt; continue;
                    case "shift": modifiers |= Shift; continue;
                }
                if (key != 0) return null; // two keys
                key = KeyCode(raw);
                if (key == 0) return null;
            }
            if (key == 0) return null;
            // a bare letter would steal typing; only function keys and PrintScreen may stand alone
            if (modifiers == 0 && !(key >= 0x70 && key <= 0x87) && key != 0x2C) return null;
            return new CaptureHotkey(modifiers, key);
        }

        private static uint KeyCode(string name)
        {
            if (name.Length == 1 && char.IsAsciiLetter(name[0])) return char.ToUpperInvariant(name[0]);
            if (name.Length == 1 && char.IsAsciiDigit(name[0])) return name[0];
            if ((name[0] == 'F' || name[0] == 'f') && int.TryParse(name.AsSpan(1), out int f) && f is >= 1 and <= 24) return (uint)(0x70 + f - 1);
            return name.ToLowerInvariant() switch { "printscreen" or "prtsc" or "prtscn" => 0x2C, _ => 0u };
        }

        public override string ToString()
        {
            var parts = new List<string>();
            if ((Modifiers & Win) != 0) parts.Add("Win");
            if ((Modifiers & Ctrl) != 0) parts.Add("Ctrl");
            if ((Modifiers & Alt) != 0) parts.Add("Alt");
            if ((Modifiers & Shift) != 0) parts.Add("Shift");
            parts.Add(VirtualKey switch
            {
                0x2C => "PrintScreen",
                >= 0x70 and <= 0x87 => "F" + (VirtualKey - 0x70 + 1),
                _ => ((char)VirtualKey).ToString()
            });
            return string.Join("+", parts);
        }
    }

    internal enum HotkeyStatus { None, Preferred, Fallback, Unavailable }

    /// <summary>Registers the capture shortcut with Windows (RegisterHotKey on a message-only window: no keyboard hook, nothing to flag in a virus scanner).</summary>
    internal sealed class HotkeyService : IDisposable
    {
        private const int WM_HOTKEY = 0x0312, HotkeyId = 0x5854;
        private const uint NoRepeat = 0x4000;
        private static readonly IntPtr MessageOnly = new(-3);

        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        private HwndSource? _source;

        /// <summary>The window that receives the shortcut message (tests post WM_HOTKEY to it).</summary>
        internal IntPtr Handle => _source?.Handle ?? IntPtr.Zero;
        private bool _registered;

        public event Action? Pressed;

        /// <summary>The shortcut that is active now, if any.</summary>
        public CaptureHotkey? Active { get; private set; }
        public HotkeyStatus Status { get; private set; }
        /// <summary>One line for Settings and the tray tooltip.</summary>
        public string Message { get; private set; } = "";

        /// <summary>Takes <paramref name="preferred"/>; when another program owns it, the first free one of <see cref="CaptureHotkey.Fallbacks"/>.</summary>
        public HotkeyStatus Apply(string preferred)
        {
            Unregister();
            _source ??= CreateWindow();
            var wanted = CaptureHotkey.Parse(preferred) ?? CaptureHotkey.Parse(CaptureHotkey.Presets[0])!.Value;
            if (TryRegister(wanted))
            {
                Status = HotkeyStatus.Preferred;
                Message = wanted + " starts a capture";
                return Status;
            }
            foreach (var fallback in CaptureHotkey.Fallbacks.Select(CaptureHotkey.Parse).Where(h => h != null && h != wanted))
            {
                if (!TryRegister(fallback!.Value)) continue;
                Status = HotkeyStatus.Fallback;
                Message = $"{wanted} is used by another program{(wanted.ToString() == "Win+C" ? " (Windows keeps Win+C for Copilot on many PCs)" : "")}: {fallback} starts a capture instead";
                return Status;
            }
            Status = HotkeyStatus.Unavailable;
            Message = $"{wanted} and the other shortcuts are used by other programs: choose another in Settings";
            return Status;
        }

        private HwndSource CreateWindow()
        {
            var source = new HwndSource(new HwndSourceParameters("XTCaptureHotkey") { ParentWindow = MessageOnly, WindowStyle = 0 });
            source.AddHook(WndProc);
            return source;
        }

        private bool TryRegister(CaptureHotkey hotkey)
        {
            if (_source == null) return false;
            _registered = RegisterHotKey(_source.Handle, HotkeyId, hotkey.Modifiers | NoRepeat, hotkey.VirtualKey);
            Active = _registered ? hotkey : null;
            return _registered;
        }

        private void Unregister()
        {
            if (_registered && _source != null) UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = false;
            Active = null;
            Status = HotkeyStatus.None;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                handled = true;
                Pressed?.Invoke();
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            Unregister();
            _source?.Dispose();
            _source = null;
        }
    }
}
