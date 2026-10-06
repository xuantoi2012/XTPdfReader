using System;

namespace XTPdfMergeApp.Controls;

internal static class ZoomRenderPrediction
{
    // Predict only towards the user's target. Bound surplus pixels independently
    // of machine RAM so a large viewport cannot multiply allocations unchecked.
    internal static double Headroom(double current, double target, double rate,
        double renderMilliseconds, double visiblePixels)
    {
        double lead = target > current && rate > 0
            ? Math.Min(target / current, Math.Exp(rate * Math.Clamp(renderMilliseconds, 50, 500) / 1000))
            : 1;
        double desired = Math.Clamp(Math.Max(1.18, lead), 1, 1.8);
        double budget = Math.Sqrt(16_000_000 / Math.Max(1, visiblePixels));
        return Math.Max(1, Math.Min(desired, budget));
    }

    internal static double Observe(double previous, double elapsed)
        => Math.Clamp(previous * .75 + Math.Clamp(elapsed, 20, 1000) * .25, 50, 500);
}
