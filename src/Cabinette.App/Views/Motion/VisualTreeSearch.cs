using System.Windows;
using System.Windows.Media;

namespace Cabinette.App.Views.Motion;

/// <summary>Depth-first searches below a visual (the root itself is not included).</summary>
internal static class VisualTreeSearch
{
    public static T? FindDescendant<T>(DependencyObject root, Func<T, bool>? match = null)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T candidate && (match is null || match(candidate)))
            {
                return candidate;
            }

            if (FindDescendant(child, match) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    public static FrameworkElement? FindByName(DependencyObject root, string name)
        => FindDescendant<FrameworkElement>(root, e => e.Name == name);

    public static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }
}
