using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ReleaseSync.Views;

public partial class ImportView : UserControl
{
    public ImportView() => InitializeComponent();

    void Drop_Click(object sender, MouseButtonEventArgs e) => App.VM.BrowseImportCommand.Execute(null);

    void Drop_Enter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text))
        {
            e.Effects = DragDropEffects.Copy;
            Drop.Background = (System.Windows.Media.Brush)FindResource("AccentSoftBrush");
        }
    }

    void Drop_Leave(object sender, DragEventArgs e) => Drop.Background = (System.Windows.Media.Brush)FindResource("InputBrush");

    async void Drop_Drop(object sender, DragEventArgs e)
    {
        Drop.Background = (System.Windows.Media.Brush)FindResource("InputBrush");
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            await App.VM.ImportFileAsync(files.First());
        else if (e.Data.GetData(DataFormats.Text) is string text)
            await App.VM.RunImportAsync(text);
    }
}
