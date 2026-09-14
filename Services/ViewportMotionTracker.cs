using System;
using System.Windows;

namespace XTPdfMergeApp.Services;

// Invalidate after meaningful accumulated movement, not every mouse event: tiny
// movements must not continuously cancel a tile before it can finish.
internal sealed class ViewportMotionTracker
{
    private Point? _anchor;
    public bool Update(Point offset, Size viewport, bool resolutionChanged)
    {
        if (_anchor is not { } anchor || resolutionChanged)
        {
            _anchor = offset;
            return false;
        }
        if (Math.Abs(offset.X - anchor.X) < Math.Max(64, viewport.Width * .35) &&
            Math.Abs(offset.Y - anchor.Y) < Math.Max(64, viewport.Height * .35)) return false;
        _anchor = offset;
        return true;
    }
}
