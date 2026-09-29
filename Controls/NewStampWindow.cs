using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using XTPdfMergeApp.Services;
using XTStyle.Controls;

namespace XTPdfMergeApp.Controls;

/// <summary>Hộp thoại "New stamp…": chữ chính, dòng phụ (tuỳ chọn) và màu.</summary>
internal sealed class NewStampWindow : XTWindow
{
    private static readonly string[] Colors = { "#C0392B", "#D9640A", "#0F8B6D", "#2563EB", "#7C3AED", "#6B7280" };
    private readonly TextBox _text, _sub;
    private string _color = Colors[0];
    private readonly Border _preview;
    private readonly TextBlock _previewText;

    public StampDefinition? Result { get; private set; }

    public NewStampWindow()
    {
        Title = "New stamp";
        TitleBarMode = TitleBarMode.Dialog;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        _text = new TextBox { Height = 30, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 10), Text = "APPROVED" };
        _sub = new TextBox { Height = 30, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 10) };
        _text.TextChanged += (_, _) => Refresh();

        var colors = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 12) };
        foreach (string hex in Colors)
        {
            string c = hex;
            var swatch = new RadioButton { GroupName = "StampColor", Margin = new Thickness(0, 0, 8, 0), IsChecked = hex == _color, Cursor = System.Windows.Input.Cursors.Hand };
            swatch.Template = SwatchTemplate((Color)ColorConverter.ConvertFromString(hex));
            swatch.Checked += (_, _) => { _color = c; Refresh(); };
            colors.Children.Add(swatch);
        }

        _previewText = new TextBlock { FontWeight = FontWeights.Bold, FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center };
        _preview = new Border { BorderThickness = new Thickness(3), CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 8, 14, 8), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 14), Child = _previewText };

        var ok = new XTButton { Text = "Add", Width = 84, Height = 32, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        ok.Click += (_, _) =>
        {
            string text = _text.Text.Trim();
            if (text.Length == 0) { _text.Focus(); return; }
            Result = new StampDefinition(StampDefinition.TextKind, text, _sub.Text.Trim(), _color, "");
            DialogResult = true;
        };
        var cancel = new XTButton { Text = "Cancel", Width = 84, Height = 32, IsCancel = true };
        cancel.Click += (_, _) => DialogResult = false;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 18) };
        panel.Children.Add(new TextBlock { Text = "Text" });
        panel.Children.Add(_text);
        panel.Children.Add(new TextBlock { Text = "Second line (optional)" });
        panel.Children.Add(_sub);
        panel.Children.Add(new TextBlock { Text = "Color" });
        panel.Children.Add(colors);
        panel.Children.Add(_preview);
        panel.Children.Add(buttons);
        Content = panel;
        Refresh();
        Loaded += (_, _) => { _text.Focus(); _text.SelectAll(); };
    }

    private void Refresh()
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_color));
        _preview.BorderBrush = brush;
        _previewText.Foreground = brush;
        _previewText.Text = _text.Text.Length == 0 ? " " : _text.Text;
    }

    private static ControlTemplate SwatchTemplate(Color color)
    {
        var template = new ControlTemplate(typeof(RadioButton));
        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.SetValue(WidthProperty, 30.0);
        grid.SetValue(HeightProperty, 30.0);
        grid.SetValue(BackgroundProperty, Brushes.Transparent);
        var ring = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse));
        ring.Name = "Ring";
        ring.SetValue(System.Windows.Shapes.Shape.StrokeProperty, Brushes.Transparent);
        ring.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 2.0);
        var dot = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse));
        dot.SetValue(WidthProperty, 20.0);
        dot.SetValue(HeightProperty, 20.0);
        dot.SetValue(System.Windows.Shapes.Shape.FillProperty, new SolidColorBrush(color));
        grid.AppendChild(ring);
        grid.AppendChild(dot);
        template.VisualTree = grid;
        var trigger = new Trigger { Property = RadioButton.IsCheckedProperty, Value = true };
        trigger.Setters.Add(new Setter(System.Windows.Shapes.Shape.StrokeProperty, Brushes.Gray, "Ring"));
        template.Triggers.Add(trigger);
        return template;
    }

    public static StampDefinition? Ask(Window? owner)
    {
        var window = new NewStampWindow { Owner = owner };
        return window.ShowDialog() == true ? window.Result : null;
    }
}
