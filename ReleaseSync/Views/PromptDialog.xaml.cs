using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ReleaseSync.Views;

public partial class PromptDialog : UserControl
{
    public PromptDialog() => InitializeComponent();

    void OnVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (Input.Visibility == Visibility.Visible) { Input.Focus(); Input.SelectAll(); }
            });
    }
}
