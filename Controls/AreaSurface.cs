using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Media;

namespace XTPdfMergeApp.Controls;

/// <summary>
/// Where a tool that needs a rectangle on a page (Find in area, Stamp pages) shows and edits it. The reader's own page view is the real one
/// (<see cref="ReaderAreaSurface"/>); <see cref="PageAreaPicker"/> is the stand-alone picture used by the tests. Positions are fractions 0..1 of the page.
/// </summary>
internal interface IAreaSurface
{
    (double U1, double V1, double U2, double V2)? Area { get; }
    void SetArea((double U1, double V1, double U2, double V2)? area);
    /// <summary>True: the rectangle is a stamp (click puts it, corners resize keeping the shape). False: drag any rectangle.</summary>
    bool PlaceMode { get; set; }
    /// <summary>Width / height of the stamp on the page.</summary>
    double PlaceAspect { get; set; }
    /// <summary>The picture shown inside the placed box (a signature).</summary>
    ImageSource? PlaceImage { get; set; }
    string CurrentPath { get; }
    /// <summary>The page the rectangle is shown on (1 based, in <see cref="CurrentPath"/>).</summary>
    int CurrentPage { get; }
    /// <summary>Height / width of the page picture on screen.</summary>
    double PageAspect { get; }
    void SetPageSize(double widthPt);
    /// <summary>Shows one of these pages (<paramref name="start"/> when given); no move when the one already shown is among them.</summary>
    Task SetPagesAsync(string path, IReadOnlyList<int> pages, int? start = null);
    bool SamePages(IReadOnlyList<int> pages);
    void ShowMarks(IEnumerable<(double U1, double V1, double U2, double V2)> boxes);
    event Action? AreaChanged;
    /// <summary>The page the rectangle is shown on changed (the user scrolled, or clicked another page).</summary>
    event Action? PageChanged;
}
