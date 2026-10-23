using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Forms = System.Windows.Forms;

namespace ReleaseSync.Views;

public sealed class SectionChecked : IMultiValueConverter
{
    public static readonly SectionChecked Instance = new();
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length > 1 && string.Equals(values[0]?.ToString(), values[1]?.ToString(), StringComparison.OrdinalIgnoreCase);
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    void Token_Changed(object sender, RoutedEventArgs e) =>
        TokenHint.Visibility = TokenBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    async void SaveToken_Click(object sender, RoutedEventArgs e)
    {
        await App.VM.SaveTokenAsync(TokenBox.Password);
        if (App.VM.Settings.HasToken) TokenBox.Clear();
    }

    void BrowseSync_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new Forms.FolderBrowserDialog { Description = "Choose a folder that's synced between your PCs (OneDrive, Dropbox…)", UseDescriptionForTitle = true, SelectedPath = App.VM.Settings.SyncFolder ?? "" };
        if (dlg.ShowDialog() == Forms.DialogResult.OK) App.VM.Settings.SyncFolder = dlg.SelectedPath;
    }

    void Vt_Changed(object sender, RoutedEventArgs e) =>
        VtHint.Visibility = VtBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    void SaveVt_Click(object sender, RoutedEventArgs e)
    {
        var key = VtBox.Password.Trim();
        if (key.Length == 0) { App.VM.Toast("Paste your VirusTotal key first", "warn"); return; }
        App.VM.Settings.VirusTotalKey = key;
        VtBox.Clear();
        App.VM.Toast("VirusTotal key saved", "success");
    }

    void ClearToken_Click(object sender, RoutedEventArgs e) => App.VM.ClearToken();

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var which = (sender as FrameworkElement)?.Tag?.ToString();
        var s = App.VM.Settings;
        using var dlg = new Forms.FolderBrowserDialog { Description = which == "apps" ? "Choose where portable apps are installed" : "Choose where downloads are stored", UseDescriptionForTitle = true, SelectedPath = which == "apps" ? s.InstallRoot : s.DownloadFolder };
        if (dlg.ShowDialog() == Forms.DialogResult.OK)
        {
            if (which == "apps") s.InstallRoot = dlg.SelectedPath; else s.DownloadFolder = dlg.SelectedPath;
        }
    }
}

