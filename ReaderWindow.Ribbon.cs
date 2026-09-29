using System.Windows;

namespace XTPdfMergeApp
{
    /// <summary>
    /// Ribbon tabs (File/Home/Comment/View/Organize) like Foxit: one group row is visible at a time, switched by the small
    /// tab strip above it. Not a real TabControl — the existing buttons/handlers just move under named panels, so nothing
    /// else in the codebase has to change. Picking a tool from another tab (shortcut, command palette) switches to it too.
    /// </summary>
    public partial class ReaderWindow
    {
        private enum RibbonTab { File, Home, Comment, View, Organize }
        private RibbonTab _ribbonTab = RibbonTab.Home;

        private void RibbonFileTab_Click(object sender, RoutedEventArgs e) => SetRibbonTab(RibbonTab.File);
        private void RibbonHomeTab_Click(object sender, RoutedEventArgs e) => SetRibbonTab(RibbonTab.Home);
        private void RibbonCommentTab_Click(object sender, RoutedEventArgs e) => SetRibbonTab(RibbonTab.Comment);
        private void RibbonViewTab_Click(object sender, RoutedEventArgs e) => SetRibbonTab(RibbonTab.View);
        private void RibbonOrganizeTab_Click(object sender, RoutedEventArgs e) => SetRibbonTab(RibbonTab.Organize);

        private void SetRibbonTab(RibbonTab tab)
        {
            _ribbonTab = tab;
            RibbonFileTabButton.Tag = tab == RibbonTab.File ? "Active" : null;
            RibbonHomeTabButton.Tag = tab == RibbonTab.Home ? "Active" : null;
            RibbonCommentTabButton.Tag = tab == RibbonTab.Comment ? "Active" : null;
            RibbonViewTabButton.Tag = tab == RibbonTab.View ? "Active" : null;
            RibbonOrganizeTabButton.Tag = tab == RibbonTab.Organize ? "Active" : null;
            RibbonFileTab.Visibility = tab == RibbonTab.File ? Visibility.Visible : Visibility.Collapsed;
            RibbonHomeTab.Visibility = tab == RibbonTab.Home ? Visibility.Visible : Visibility.Collapsed;
            RibbonCommentTab.Visibility = tab == RibbonTab.Comment ? Visibility.Visible : Visibility.Collapsed;
            RibbonViewTab.Visibility = tab == RibbonTab.View ? Visibility.Visible : Visibility.Collapsed;
            RibbonOrganizeTab.Visibility = tab == RibbonTab.Organize ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RibbonExport_Click(object sender, RoutedEventArgs e) => OpenExport(preferFlatten: false);

        /// <summary>Switches to the tab that owns <paramref name="tool"/> (Comment tools live in the Comment tab, everything
        /// else in Home) — so picking one by keyboard shortcut or the command palette shows it lit up where the user expects.</summary>
        private void ShowRibbonTabForTool(ReaderTool tool)
        {
            var tab = tool switch
            {
                ReaderTool.Typewriter or ReaderTool.Comment or ReaderTool.Highlight or ReaderTool.Underline
                    or ReaderTool.Strikethrough or ReaderTool.Shape or ReaderTool.Stamp => RibbonTab.Comment,
                _ => RibbonTab.Home
            };
            if (tab != _ribbonTab) SetRibbonTab(tab);
        }
    }
}
