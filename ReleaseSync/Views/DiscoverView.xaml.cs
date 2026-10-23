using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ReleaseSync.Views;

public partial class DiscoverView : UserControl
{
    public DiscoverView()
    {
        InitializeComponent();
        Loaded += (_, _) => App.VM.RateDropped += OnRateDropped;
        Unloaded += (_, _) => App.VM.RateDropped -= OnRateDropped;
    }

    /// <summary>Each time requests are used, the counter glows red for a moment and settles back.</summary>
    void OnRateDropped(int used)
    {
        Dispatcher.BeginInvoke(new System.Action(() =>
        {
            var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0, System.TimeSpan.FromMilliseconds(1100));
            RateNumFlash.BeginAnimation(OpacityProperty, fade);
            RateGlow.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0.28, 0, System.TimeSpan.FromMilliseconds(1100)));
        }));
    }

    void Back_Click(object sender, RoutedEventArgs e) => App.VM.ResetDiscover();

    void Card_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ReleaseSync.Core.RepoInfo r) App.VM.DetailsCommand.Execute(r);
    }

    /// <summary>The "…" button on a card: opens the shared menu for that project.</summary>
    void RepoMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ReleaseSync.Core.RepoInfo)
        {
            var menu = (ContextMenu)FindResource("RepoMenu");
            menu.DataContext = fe.DataContext;
            menu.PlacementTarget = fe;
            menu.Placement = PlacementMode.Bottom;
            RoutedEventHandler? closed = null;
            closed = (_, _) => { menu.Closed -= closed; menu.ClearValue(DataContextProperty); };      // one menu serves every card
            menu.Closed += closed;
            menu.IsOpen = true;
        }
        e.Handled = true;
    }

    /// <summary>Opens the try-later drawer under its button, lined up with the button's right edge.</summary>
    void TryBtn_Click(object sender, RoutedEventArgs e)
    {
        TryPop.HorizontalOffset = TryBtn.ActualWidth - 380 - 28;
        TryPop.IsOpen = !TryPop.IsOpen;
    }

    void Help_Click(object sender, RoutedEventArgs e)
    {
        HelpPop.HorizontalOffset = HelpBtn.ActualWidth - 440 - 28;
        HelpPop.IsOpen = !HelpPop.IsOpen;
    }

    void HelpClose_Click(object sender, RoutedEventArgs e) => HelpPop.IsOpen = false;

    void TryRow_Click(object sender, RoutedEventArgs e) => TryPop.IsOpen = false;
}