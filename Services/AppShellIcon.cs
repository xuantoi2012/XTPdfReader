using System;
using System.Linq;
using System.Windows.Media.Imaging;

namespace XTPdfMergeApp.Services;

internal static class AppShellIcon
{
    // Keep the ICO decoder so WPF can select the best native frame for the display DPI.
    private static readonly BitmapDecoder Decoder = BitmapDecoder.Create(
        new Uri("pack://application:,,,/XTPdfMergeApp;component/PDF%20icon.ico"),
        BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

    internal static BitmapFrame Image => Decoder.Frames.OrderByDescending(frame => frame.PixelWidth).First();
}
