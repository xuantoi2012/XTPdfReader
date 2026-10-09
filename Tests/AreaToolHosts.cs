using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using XTPdfMergeApp.Services;
using XTPdfMergeApp.Services.TextEdit;
using XTStyle.Controls;

namespace XTPdfMergeApp.Controls;

/// <summary>
/// Test hosts: in the app the Find in area and Stamp pages panels sit beside the reader and draw on its page; here each one sits beside a stand-alone page picture
/// (<see cref="PageAreaPicker"/>) in a window of its own, so the panel logic can be driven without the reader window.
/// </summary>
internal sealed class BatchFindWindow : XTWindow
{
    public BatchFindPanel Panel { get; }
    public PageAreaPicker Picker { get; } = new() { MinHeight = 280 };

    public BatchFindWindow(string path, int currentPage, int pageCount)
    {
        Panel = new BatchFindPanel(Picker, path, currentPage, pageCount);
        Width = 1240; Height = 820;
        Content = AreaHost.Build(Panel, Picker);
        Loaded += async (_, _) => await Panel.StartAsync();
    }

    internal PaperGroupList Sizes => Panel.Sizes;
    internal RadioButton ObjectsModeButton => Panel.ObjectsModeButton;
    internal CheckBox TouchBox => Panel.TouchBox;
    internal TextBox FindBox => Panel.FindBox;
    internal TextBox ReplaceBox => Panel.ReplaceBox;
    internal RadioButton AllPagesButton => Panel.AllPagesButton;
    internal RadioButton RangeButton => Panel.RangeButton;
    internal TextBox RangeBox => Panel.RangeBox;
    internal ObservableCollection<BatchHit> Hits => Panel.Hits;
    internal string StatusText => Panel.StatusText;
    internal XTButton ReplaceButton => Panel.ReplaceButton;
    internal XTButton DeleteButton => Panel.DeleteButton;
    internal bool ObjectsMode => Panel.ObjectsMode;
    internal IReadOnlyList<(int Page, TextRun Place, TextEdit? Edit)> Edits => Panel.Edits;
    internal string Description => Panel.Description;
    internal IReadOnlyList<PdfObjectRef> ObjectsToDelete => Panel.ObjectsToDelete;
    internal System.Threading.Tasks.Task FindAsync() => Panel.FindAsync();
    internal void ApplyForTest(bool delete) => Panel.ApplyForTest(delete);
}

internal sealed class StampPagesWindow : XTWindow
{
    public StampPagesPanel Panel { get; }
    public PageAreaPicker Picker { get; } = new() { MinHeight = 280, PlaceMode = true };

    public StampPagesWindow(string path, int currentPage, int pageCount)
    {
        Panel = new StampPagesPanel(Picker, path, currentPage, pageCount);
        Width = 1200; Height = 820;
        Content = AreaHost.Build(Panel, Picker);
        Loaded += async (_, _) => await Panel.StartAsync();
    }

    internal PaperGroupList Sizes => Panel.Sizes;
    internal ListBox StampList => Panel.StampList;
    internal TextBox NameBox => Panel.NameBox;
    internal RadioButton AllPagesButton => Panel.AllPagesButton;
    internal RadioButton RangeButton => Panel.RangeButton;
    internal TextBox RangeBox => Panel.RangeBox;
    internal XTButton ApplyButton => Panel.ApplyButton;
    internal string StatusText => Panel.StatusText;
    public StampDefinition? Definition => Panel.Definition;
    public IReadOnlyList<(int Page, double U1, double V1, double U2, double V2)> Placements => Panel.Placements;
    internal void ApplyForTest() => Panel.ApplyForTest();
}

internal static class AreaHost
{
    internal static Grid Build(UIElement panel, UIElement picture)
    {
        var grid = new Grid { Margin = new Thickness(8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(panel);
        Grid.SetColumn(picture, 1);
        grid.Children.Add(picture);
        return grid;
    }
}
