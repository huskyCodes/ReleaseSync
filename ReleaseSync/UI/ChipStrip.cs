using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ReleaseSync.UI;

/// <summary>A single row of chips that scrolls sideways when there are too many to fit, with arrow buttons that appear only when needed. Keeps filter rows one tidy line tall on every page.</summary>
public class ChipStrip : ContentControl
{
    static ChipStrip() => DefaultStyleKeyProperty.OverrideMetadata(typeof(ChipStrip), new FrameworkPropertyMetadata(typeof(ChipStrip)));

    ScrollViewer? scroll;
    Button? left, right;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        scroll = GetTemplateChild("PART_Scroll") as ScrollViewer;
        left = GetTemplateChild("PART_Left") as Button;
        right = GetTemplateChild("PART_Right") as Button;
        if (scroll == null) return;
        scroll.ScrollChanged += (_, _) => UpdateArrows();
        scroll.PreviewMouseWheel += OnWheel;
        SizeChanged += (_, _) => UpdateArrows();
        if (left != null) left.Click += (_, _) => scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - scroll.ViewportWidth * 0.7);
        if (right != null) right.Click += (_, _) => scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset + scroll.ViewportWidth * 0.7);
        Loaded += (_, _) => UpdateArrows();
    }

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (scroll == null || scroll.ScrollableWidth <= 0) return;        // nothing to scroll: let the page scroll instead
        scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    void UpdateArrows()
    {
        if (scroll == null) return;
        if (left != null) left.Visibility = scroll.HorizontalOffset > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (right != null) right.Visibility = scroll.HorizontalOffset < scroll.ScrollableWidth - 1 ? Visibility.Visible : Visibility.Collapsed;
    }
}
