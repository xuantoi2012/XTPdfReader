using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using XTStyle.Controls;
using XTPdfMergeApp.Services;

namespace XTPdfMergeApp.Controls;

/// <summary>Hộp thoại chỉ đọc, diễn giải quyền khai báo trong PDF cho người dùng.</summary>
internal sealed class DocumentSecurityWindow : XTWindow
{
    public bool ManageRequested { get; private set; }

    public DocumentSecurityWindow(string fileName, PdfSecurityInfo info)
    {
        Title = "Document security";
        TitleBarMode = TitleBarMode.Tool;
        Width = 500;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        Background = (Brush)Application.Current.FindResource("Ui.Bg");

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 24) };
        panel.Children.Add(new TextBlock
        {
            Text = fileName,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        if (info.Error != null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Could not read the document security settings. " + info.Error,
                Margin = new Thickness(0, 12, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.IndianRed
            });
            Content = panel;
            return;
        }

        panel.Children.Add(new TextBlock
        {
            Text = !info.IsEncrypted ? "Not encrypted — no PDF permission restrictions declared."
                : info.IsOwner ? "Encrypted — opened with owner permissions."
                : "Encrypted — opened with user permissions.",
            Margin = new Thickness(0, 10, 0, 14),
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(Row("Print", info.CanPrint));
        panel.Children.Add(Row("Copy text and graphics", info.CanCopy));
        panel.Children.Add(Row("Modify document", info.CanModify));
        panel.Children.Add(Row("Add or edit comments", info.CanAnnotate));
        panel.Children.Add(Row("Fill forms", info.CanFillForms));

        if (info.IsEncrypted)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "These are permissions declared by the PDF. Certification-signature validation is not included yet.",
                Margin = new Thickness(0, 16, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                FontSize = 12
            });
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var manage = new XTButton { Text = info.IsEncrypted ? "Change protection…" : "Protect PDF…", Width = 146, Height = 32, Margin = new Thickness(0, 0, 8, 0) };
        manage.Click += (_, _) => { ManageRequested = true; DialogResult = true; };
        var close = new XTButton { Text = "Close", Width = 84, Height = 32, IsCancel = true };
        buttons.Children.Add(manage);
        buttons.Children.Add(close);
        panel.Children.Add(buttons);
        Content = panel;
    }

    private static DockPanel Row(string label, bool allowed)
    {
        var row = new DockPanel { Margin = new Thickness(0, 5, 0, 0) };
        var status = new TextBlock
        {
            Text = allowed ? "Allowed" : "Not allowed",
            Foreground = allowed ? Brushes.ForestGreen : Brushes.IndianRed,
            FontWeight = FontWeights.SemiBold
        };
        DockPanel.SetDock(status, Dock.Right);
        row.Children.Add(status);
        row.Children.Add(new TextBlock { Text = label });
        return row;
    }
}
