using System.Windows;
using XTStyle.Controls;

namespace XTPdfMergeApp.Services
{
    /// <summary>The notifications of the app (XTGrowl) with their text in the language of the interface.</summary>
    internal static class Growl
    {
        public static void Success(string message, Window? owner = null, string? title = null) => XTGrowl.Success(Loc.T(message), owner!, title == null ? null! : Loc.T(title));
        public static void Info(string message, Window? owner = null, string? title = null) => XTGrowl.Info(Loc.T(message), owner!, title == null ? null! : Loc.T(title));
        public static void Warning(string message, Window? owner = null, string? title = null) => XTGrowl.Warning(Loc.T(message), owner!, title == null ? null! : Loc.T(title));
        public static void Error(string message, Window? owner = null, string? title = null) => XTGrowl.Error(Loc.T(message), owner!, title == null ? null! : Loc.T(title));
    }
}
