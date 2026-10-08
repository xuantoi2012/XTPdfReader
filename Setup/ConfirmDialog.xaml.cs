using System.Windows;
using System.Windows.Media;

namespace PdfReaderSetup;

/// <summary>The installer's own message box: same look as the main window, two buttons.</summary>
public partial class ConfirmDialog : Window
{
    private ConfirmDialog(string title, string message, string actionText, string safeText, bool actionIsPrimary)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        // Order on screen: the safe answer on the left, the action on the right.
        var green = (Brush)FindResource("Green");
        var pale = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF));
        var dark = new SolidColorBrush(Color.FromRgb(0x16, 0x2B, 0x62));
        SetButton(FirstButton, safeText, actionIsPrimary ? pale : green, actionIsPrimary ? dark : Brushes.White);
        SetButton(SecondButton, actionText, actionIsPrimary ? green : pale, actionIsPrimary ? Brushes.White : dark);
        (actionIsPrimary ? SecondButton : FirstButton).IsDefault = true;
        FirstButton.IsCancel = true; // Esc = the safe answer
        Loaded += (_, _) => (actionIsPrimary ? SecondButton : FirstButton).Focus();
    }

    private static void SetButton(System.Windows.Controls.Button button, string text, Brush background, Brush foreground)
    {
        button.Content = text;
        button.Background = background;
        button.Foreground = foreground;
    }

    private void First_Click(object sender, RoutedEventArgs e) { DialogResult = false; }

    private void Second_Click(object sender, RoutedEventArgs e) { DialogResult = true; }

    /// <summary>True when the user chose the action (<paramref name="actionText"/>), false for the safe answer or Esc.</summary>
    internal static bool Ask(Window owner, string title, string message, string actionText, string safeText, bool actionIsPrimary)
        => new ConfirmDialog(title, message, actionText, safeText, actionIsPrimary) { Owner = owner }.ShowDialog() == true;
}
