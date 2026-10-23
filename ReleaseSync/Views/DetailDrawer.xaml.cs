using System.Windows;
using System.Windows.Controls;
using ReleaseSync.Core;

namespace ReleaseSync.Views;

public partial class DetailDrawer : UserControl
{
    public DetailDrawer() => InitializeComponent();

    void Asset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox cb || !(cb.IsDropDownOpen || cb.IsKeyboardFocusWithin)) return;
        if (cb.DataContext is not TrackedApp a || e.AddedItems.Count == 0 || e.AddedItems[0] is not AssetInfo choice) return;
        var pattern = choice.IsAutomatic ? null : AssetPicker.MakePattern(choice.Name);
        if (pattern == a.AssetPattern) return;
        a.AssetPattern = pattern;
        a.NotifyAll();
        App.VM.ScheduleSaveApps();
    }

    async void Prerelease_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is TrackedApp a)
        {
            await App.VM.CheckAppAsync(a, true);
            a.NotifyAll();
            App.VM.ScheduleSaveApps();
        }
    }
}
