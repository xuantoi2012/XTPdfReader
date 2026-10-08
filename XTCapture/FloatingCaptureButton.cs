using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace XTCapture
{
    /// <summary>
    /// The small capture button in the bottom right corner of the screen: half see-through until the pointer is over it, always on top, never takes the
    /// focus. A click starts a capture; drag it to move it (the place is remembered); right-click for the menu.
    /// </summary>
    internal sealed class FloatingCaptureButton : Window
    {
        internal const double Size = 48, RestingOpacity = 0.5;

        private readonly Border _disc;
        private Point _pressAt;
        private Point _windowAtPress;
        private bool _pressed, _dragged;

        public event Action? Clicked;
        public event Action? StoreRequested, SettingsRequested, HideRequested, ExitRequested;

        internal bool WasDragged => _dragged;

        public FloatingCaptureButton()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            Width = Height = Size;
            Opacity = RestingOpacity;
            ToolTip = "Capture the screen";
            Cursor = Cursors.Hand;
            NativeWindowStyles.NoActivate(this);

            var icon = CaptureIcons.Create(CaptureIcons.Capture, Brushes.White, 24);
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
            _disc = new Border
            {
                Width = Size - 8, Height = Size - 8, CornerRadius = new CornerRadius(Size / 2), Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 255, 255, 255)), BorderThickness = new Thickness(1), Child = icon,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.4, Direction = 270 }
            };
            Content = _disc;

            MouseEnter += (_, _) => BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
            MouseLeave += (_, _) => { if (!_pressed) BeginAnimation(OpacityProperty, new DoubleAnimation(RestingOpacity, TimeSpan.FromMilliseconds(250))); };
            MouseLeftButtonDown += (_, e) => { OnPress(PointToScreen(e.GetPosition(this))); CaptureMouse(); e.Handled = true; };
            MouseMove += (_, e) => { if (_pressed) OnDrag(PointToScreen(e.GetPosition(this))); };
            MouseLeftButtonUp += (_, e) => { ReleaseMouseCapture(); OnRelease(); e.Handled = true; };
            ContextMenu = BuildMenu();
            Loaded += (_, _) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            Closed += (_, _) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            Place();
        }

        private ContextMenu BuildMenu()
        {
            var menu = new ContextMenu();
            MenuItem Item(string text, Action? action) { var item = new MenuItem { Header = text }; item.Click += (_, _) => action?.Invoke(); return item; }
            menu.Items.Add(Item("Capture", () => Clicked?.Invoke()));
            menu.Items.Add(Item("Store", () => StoreRequested?.Invoke()));
            menu.Items.Add(Item("Settings", () => SettingsRequested?.Invoke()));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Hide this button", () => HideRequested?.Invoke()));
            menu.Items.Add(Item("Exit XT Capture", () => ExitRequested?.Invoke()));
            return menu;
        }

        // ── Place ───────────────────────────────────────────────────────

        /// <summary>The default place: the bottom right corner of the main screen's work area (above the taskbar).</summary>
        internal static Point DefaultPosition()
        {
            var area = SystemParameters.WorkArea;
            return new Point(area.Right - Size - 16, area.Bottom - Size - 16);
        }

        /// <summary>The remembered place, if it is still on a screen; else the default.</summary>
        internal static Point ResolvePosition((int X, int Y) saved)
        {
            if (saved.X < 0 && saved.Y < 0) return DefaultPosition();
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var wanted = new Rect(saved.X, saved.Y, Size, Size);
            return screen.Contains(wanted) ? new Point(saved.X, saved.Y) : DefaultPosition();
        }

        private void Place()
        {
            var at = ResolvePosition(CaptureSettings.FloatingButtonPosition);
            Left = at.X;
            Top = at.Y;
        }

        private void OnDisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(Place));

        // ── Click or drag (internal for the tests) ──────────────────────

        internal void OnPress(Point screenPoint)
        {
            _pressed = true;
            _dragged = false;
            _pressAt = screenPoint;
            _windowAtPress = new Point(Left, Top);
        }

        internal void OnDrag(Point screenPoint)
        {
            double dx = screenPoint.X - _pressAt.X, dy = screenPoint.Y - _pressAt.Y;
            if (!_dragged && Math.Abs(dx) + Math.Abs(dy) < 6) return; // a click that wobbled is still a click
            _dragged = true;
            // screen points are in pixels, the window in DIPs
            var source = PresentationSource.FromVisual(this);
            double scale = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1;
            Left = _windowAtPress.X + dx * scale;
            Top = _windowAtPress.Y + dy * scale;
        }

        internal void OnRelease()
        {
            if (!_pressed) return;
            _pressed = false;
            if (_dragged)
            {
                CaptureSettings.FloatingButtonPosition = ((int)Math.Round(Left), (int)Math.Round(Top));
                if (!IsMouseOver) BeginAnimation(OpacityProperty, new DoubleAnimation(RestingOpacity, TimeSpan.FromMilliseconds(250)));
                return;
            }
            Clicked?.Invoke();
        }
    }
}
