using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ReleaseSync.Views;

public partial class TryLaterView : UserControl
{
    public TryLaterView() => InitializeComponent();

    void Hdr_Loaded(object sender, RoutedEventArgs e) { if (sender is ToggleButton t) GroupFold.HeaderLoaded("trylater", t); }
    void Hdr_Click(object sender, RoutedEventArgs e) { if (sender is ToggleButton t) GroupFold.HeaderClicked("trylater", t); }
    void FoldAll_Click(object sender, RoutedEventArgs e) => GroupFold.SetAll("trylater", Items, false);
    void OpenAll_Click(object sender, RoutedEventArgs e) => GroupFold.SetAll("trylater", Items, true);
}