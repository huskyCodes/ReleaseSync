using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace ReleaseSync.Views;

/// <summary>Remembers which group sections are folded on each page, so they stay folded when the list is filtered or refreshed, and lets a page fold or open everything at once.</summary>
public static class GroupFold
{
    static readonly Dictionary<string, HashSet<string>> folded = new();

    static HashSet<string> For(string page)
    {
        if (!folded.TryGetValue(page, out var s)) folded[page] = s = new HashSet<string>();
        return s;
    }

    static string NameOf(FrameworkElement header) => (header.DataContext as CollectionViewGroup)?.Name?.ToString() ?? "";

    /// <summary>A section header was created: open or fold it the way the user left it.</summary>
    public static void HeaderLoaded(string page, ToggleButton header)
    {
        if (header.DataContext is CollectionViewGroup) header.IsChecked = !For(page).Contains(NameOf(header));
    }

    /// <summary>The user clicked a section header.</summary>
    public static void HeaderClicked(string page, ToggleButton header)
    {
        if (header.DataContext is not CollectionViewGroup) return;
        var set = For(page);
        if (header.IsChecked == true) set.Remove(NameOf(header)); else set.Add(NameOf(header));
    }

    /// <summary>Fold or open every section on the page.</summary>
    public static void SetAll(string page, ItemsControl list, bool open)
    {
        var set = For(page);
        set.Clear();
        if (!open && list.Items is CollectionView view && view.Groups != null)
            foreach (var g in view.Groups.OfType<CollectionViewGroup>()) set.Add(g.Name?.ToString() ?? "");
        foreach (var header in Headers(list)) header.IsChecked = open;
    }

    static IEnumerable<ToggleButton> Headers(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ToggleButton tb && tb.Name == "hdr") yield return tb;
            foreach (var inner in Headers(child)) yield return inner;
        }
    }
}
