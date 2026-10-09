using System.IO;
using System.Windows;
using XTPdfMergeApp.Services;

internal static partial class Program
{
    /// <summary>
    /// The Vietnamese interface: opens the reader, Settings, the Start page and both tool panels in Vietnamese and lists the English texts that were left without a translation
    /// (written to results/untranslated.txt). Also checks that switching back to English restores the original texts.
    /// </summary>
    static void TestLocalizationCoverage()
    {
        UseTestAreaStore("loc");
        string source = BuildTwoSizesPdf(System.IO.Path.Combine(Output, "loc-source"));
        RunReaderFlow("loc", async f =>
        {
            var window = f.Window;
            await Task.Delay(1200);
            Loc.Use("vi");
            Loc.Missing = new HashSet<string>();
            try
            {
                var settingsPage = new XTPdfMergeApp.Controls.SettingsPage();
                var settingsHost = new System.Windows.Window { Content = settingsPage, Width = 800, Height = 600, Left = -32000, Top = -32000, ShowActivated = false, WindowStartupLocation = System.Windows.WindowStartupLocation.Manual };
                settingsHost.Show();
                await Task.Delay(300);
                Loc.ApplyTree(settingsHost);
                settingsHost.Close();
                f.Call("ShowStart", true);
                await Task.Delay(300);
                Loc.ApplyTree(window);
                f.Call("ShowStart", false);
                foreach (var tab in new[] { "Pages", "Bookmarks", "Layers", "Comments", "History", "Sheets" })
                {
                    ((XTPdfMergeApp.Controls.ReaderSidePanel)window.FindName("ReaderSidePanel")!).ShowPanel(tab);
                    await Task.Delay(200);
                    Loc.ApplyTree(window);
                }
                var print = new XTPdfMergeApp.Controls.PrintWindow(new[] { (f.Path, 1), (f.Path, 2) }, 0) { Left = -32000, Top = -32000, ShowActivated = false, WindowStartupLocation = System.Windows.WindowStartupLocation.Manual };
                print.Show();
                await Task.Delay(1500);
                Loc.ApplyTree(print);
                print.Close();
                f.Call("OpenStampPages");
                await Task.Delay(2000);
                Loc.ApplyTree(window);
                f.Call("OpenBatchFind");
                await Task.Delay(2000);
                Loc.ApplyTree(window);
                f.Call("CloseToolPanel");

                var title = (System.Windows.Controls.TextBlock)f.Window.FindName("ReaderToolPanelTitle")!;
                Check(Loc.T("Find in area") == "Tìm trong vùng" && Loc.T("Stamped 3 pages. Ctrl+S saves them; Undo takes them all back; drag a corner to resize one.").StartsWith("Đã đóng dấu 3 trang."), "Sentences and numbers are translated");
                Check(Loc.T("Exported 1 page to:\nC:\\a.pdf") .StartsWith("Đã xuất 1 trang"), "A message with a path keeps the path");

                Directory.CreateDirectory(Output);
                File.WriteAllText(System.IO.Path.Combine(Output, "untranslated.txt"), string.Join("\n", Loc.Missing.OrderBy(m => m)));
                Console.WriteLine($"Untranslated texts on screen: {Loc.Missing.Count} (see untranslated.txt)");
                Check(Loc.Missing.Count(m => !m.Contains(" mm") && !m.Contains("inch") && !System.Text.RegularExpressions.Regex.IsMatch(m, @"\d\s*[×x]\s*\d")) < 25, $"Almost every text on screen is translated ({Loc.Missing.Count} are not: {string.Join(" | ", Loc.Missing.Where(m => !m.Contains(" mm)")).OrderBy(m => m).Take(12))})");

                // back to English: the source texts return
                Loc.Use("en");
                Loc.ApplyTree(window);
                var palette = (System.Windows.Controls.Control)window.FindName("TitleSearchButton")!;
                Check(((XTStyle.Controls.XTButton)palette).Text.StartsWith("Search commands"), "English again after the switch: " + ((XTStyle.Controls.XTButton)palette).Text);
            }
            finally { Loc.Missing = null; Loc.Use("en"); }
        }, copyFrom: source);
    }
}

internal static partial class Program
{
    /// <summary>The language chosen last time is applied to the reader window as it opens (not only to the splash).</summary>
    static void TestLocalizationOnStart()
    {
        if (System.Windows.Application.Current == null) CreateReaderTestApplication();
        Loc.Use("vi");
        Loc.Attach();
        try
        {
            RunReaderFlow("locstart", async f =>
            {
                await Task.Delay(1500);
                var search = (XTStyle.Controls.XTButton)f.Window.FindName("TitleSearchButton")!;
                Check(search.Text.StartsWith("Tìm lệnh"), "The search box is Vietnamese as the window opens: " + search.Text);
            });
        }
        finally { Loc.Use("en"); }
    }
}
