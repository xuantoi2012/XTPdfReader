using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace XTPdfMergeApp.Services
{
    /// <summary>Tiện ích duyệt visual tree DÙNG CHUNG giữa MainWindow (merge/sắp xếp) và
    /// ReaderWindow (xem PDF) — tách ra khỏi cả 2 để không phải chép trùng, vì cả code kéo-thả
    /// trang/SplitHostGrid (MainWindow) lẫn code tile/continuous-mode (ReaderWindow) đều cần.
    /// Dùng "using static XTPdfMergeApp.Services.VisualTreeHelpers;" ở nơi gọi để giữ nguyên cách
    /// gọi không cần tiền tố lớp, khớp với code cũ trước khi tách.</summary>
    internal static class VisualTreeHelpers
    {
        public static ScrollViewer? FindPageScrollViewer(ListBox listBox)
        {
            listBox.ApplyTemplate();
            return FindVisualChild<ScrollViewer>(listBox);
        }

        public static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typed) return typed;
                var found = FindVisualChild<T>(child);
                if (found != null) return found;
            }
            return null;
        }

        public static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typed) yield return typed;
                foreach (var descendant in FindVisualChildren<T>(child))
                    yield return descendant;
            }
        }

        public static T? FindVisualChildByName<T>(DependencyObject parent, string name) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typed && (child as FrameworkElement)?.Name == name) return typed;
                var found = FindVisualChildByName<T>(child, name);
                if (found != null) return found;
            }
            return null;
        }

        public static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            var parent = VisualTreeHelper.GetParent(child);
            while (parent != null && parent is not T)
                parent = VisualTreeHelper.GetParent(parent);
            return parent as T;
        }

        public static FrameworkElement? FindVisualParentByName(DependencyObject child, string name)
        {
            var parent = VisualTreeHelper.GetParent(child);
            while (parent != null)
            {
                if (parent is FrameworkElement fe && fe.Name == name) return fe;
                parent = VisualTreeHelper.GetParent(parent);
            }
            return null;
        }

        public static T? FindSelfOrVisualParent<T>(DependencyObject? source) where T : DependencyObject
        {
            while (source != null)
            {
                if (source is T match) return match;
                source = VisualTreeHelper.GetParent(source);
            }
            return null;
        }

        public static ListBoxItem? FindListBoxItem(DependencyObject? source)
        {
            while (source != null && source is not ListBoxItem)
                source = VisualTreeHelper.GetParent(source);
            return source as ListBoxItem;
        }

        public static bool IsFromScrollChrome(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is ScrollBar || source is Thumb || source is Track || source is RepeatButton)
                    return true;

                source = VisualTreeHelper.GetParent(source);
            }

            return false;
        }

        public static bool IsElementInViewport(FrameworkElement element, Visual viewportHost, Rect viewport)
        {
            try
            {
                var bounds = element.TransformToAncestor(viewportHost)
                    .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                return bounds.Width > 0 && bounds.Height > 0 && viewport.IntersectsWith(bounds);
            }
            catch
            {
                return false;
            }
        }
    }
}
