using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace XTCapture
{
    /// <summary>A short message in the bottom right corner ("Copied. Kept in the Store."): it never takes focus and goes away by itself.</summary>
    internal sealed class ToastWindow : Window
    {
        private static ToastWindow? _current;

        /// <summary>Tests: nothing is shown, the text is only recorded.</summary>
        internal static bool Suppress { get; set; }
        internal static string? LastText { get; private set; }

        internal TextBlock Label { get; } = new() { Foreground = Brushes.White, FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 };

        private ToastWindow(string text)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            Label.Text = text;
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x1F, 0x29, 0x37)), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 9, 14, 9), Margin = new Thickness(10),
                Child = Label, Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.35, Direction = 270 }
            };
            NativeWindowStyles.NoActivate(this);
            Loaded += (_, _) =>
            {
                var area = SystemParameters.WorkArea;
                Left = area.Right - ActualWidth - 12;
                Top = area.Bottom - ActualHeight - 84; // above the corner capture button
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
                    fade.Completed += (_, _) => Close();
                    BeginAnimation(OpacityProperty, fade);
                };
                timer.Start();
            };
            Closed += (_, _) => { if (ReferenceEquals(_current, this)) _current = null; };
        }

        public static ToastWindow? Display(string text)
        {
            LastText = text;
            if (Suppress) return null;
            _current?.Close();
            _current = new ToastWindow(text);
            _current.Show();
            return _current;
        }
    }
}
