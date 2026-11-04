using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using ReleaseSync.Core;
using Microsoft.Win32;

namespace ReleaseSync.UI;

// ---------------- attached properties used by control templates ----------------
public static class Ext
{
    public static readonly DependencyProperty HintProperty = DependencyProperty.RegisterAttached("Hint", typeof(string), typeof(Ext), new PropertyMetadata(""));
    public static string GetHint(DependencyObject o) => (string)o.GetValue(HintProperty);
    public static void SetHint(DependencyObject o, string v) => o.SetValue(HintProperty, v);

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached("Glyph", typeof(string), typeof(Ext), new PropertyMetadata(""));
    public static string GetGlyph(DependencyObject o) => (string)o.GetValue(GlyphProperty);
    public static void SetGlyph(DependencyObject o, string v) => o.SetValue(GlyphProperty, v);

    public static readonly DependencyProperty BadgeProperty = DependencyProperty.RegisterAttached("Badge", typeof(int), typeof(Ext), new PropertyMetadata(0));
    public static int GetBadge(DependencyObject o) => (int)o.GetValue(BadgeProperty);
    public static void SetBadge(DependencyObject o, int v) => o.SetValue(BadgeProperty, v);
}

public class SettingRow : ContentControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingRow));
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingRow));
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    static SettingRow() => DefaultStyleKeyProperty.OverrideMetadata(typeof(SettingRow), new FrameworkPropertyMetadata(typeof(SettingRow)));
}

/// <summary>Lays children out in equal-width columns; the column count adapts to the available width.</summary>
public class ResponsivePanel : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(nameof(MinItemWidth), typeof(double), typeof(ResponsivePanel),
        new FrameworkPropertyMetadata(320.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(ResponsivePanel),
        new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));
    public double MinItemWidth { get => (double)GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    (int cols, double w) Layout(double avail)
    {
        if (double.IsInfinity(avail) || avail <= 0) return (1, MinItemWidth);
        var min = Math.Max(120, Math.Min(MinItemWidth, avail));
        int cols = Math.Max(1, (int)((avail + Spacing) / (min + Spacing)));
        double w = (avail - (cols - 1) * Spacing) / cols;
        return (cols, w);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (cols, w) = Layout(availableSize.Width);
        double total = 0; int i = 0; double rowH = 0; int rows = 0;
        foreach (UIElement c in InternalChildren)
        {
            c.Measure(new Size(w, double.PositiveInfinity));
            rowH = Math.Max(rowH, c.DesiredSize.Height);
            if (++i % cols == 0) { total += rowH; rowH = 0; rows++; }
        }
        if (i % cols != 0) { total += rowH; rows++; }
        total += Math.Max(0, rows - 1) * Spacing;
        return new Size(double.IsInfinity(availableSize.Width) ? w : availableSize.Width, total);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (cols, w) = Layout(finalSize.Width);
        var kids = InternalChildren.Cast<UIElement>().ToList();
        double y = 0;
        for (int start = 0; start < kids.Count; start += cols)
        {
            var row = kids.Skip(start).Take(cols).ToList();
            double h = row.Max(k => k.DesiredSize.Height);
            for (int c = 0; c < row.Count; c++) row[c].Arrange(new Rect(c * (w + Spacing), y, w, h));
            y += h + Spacing;
        }
        return finalSize;
    }
}

// ---------------- converters ----------------
public class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }
    public bool Collapse { get; set; } = true;
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool b = value is bool v && v;
        if (Invert) b = !b;
        return b ? Visibility.Visible : (Collapse ? Visibility.Collapsed : Visibility.Hidden);
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>The tallest a menu may be on this screen, leaving room above and below it. Anything longer scrolls.</summary>
public sealed class MenuHeight : IValueConverter
{
    public static readonly MenuHeight Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is double h ? Math.Max(240, h * 0.8) : 520d;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class NullToVisibility : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool has = value != null && !(value is string s && s.Length == 0) && !(value is int i && i == 0);
        if (Invert) has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public class EqualsConverter : IValueConverter
{
    public bool ToVisibility { get; set; }
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool eq = string.Equals(value?.ToString(), p?.ToString(), StringComparison.OrdinalIgnoreCase);
        if (Invert) eq = !eq;
        if (ToVisibility) return eq ? Visibility.Visible : Visibility.Collapsed;
        return eq;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is true ? p : Binding.DoNothing;
}

public class EqualMultiVisibility : IMultiValueConverter
{
    public object Convert(object[] values, Type t, object p, CultureInfo c) =>
        values.Length > 1 && string.Equals(values[0]?.ToString(), values[1]?.ToString(), StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
    public object[] ConvertBack(object value, Type[] t, object p, CultureInfo c) => throw new NotSupportedException();
}

public class InvertBool : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => !(value is bool b && b);
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => !(value is bool b && b);
}

public class PercentConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is double d ? $"{Math.Round(d * 100)}%" : "";
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public class AccentSwatchConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(value?.ToString() ?? "#6C8CFF"));
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

// ---------------- theming ----------------
public static class ThemeManager
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static bool IsDark { get; private set; } = true;

    static bool SystemUsesLight()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    public static void Apply(AppSettings s)
    {
        var app = Application.Current;
        bool light = s.Theme == "Light" || (s.Theme == "System" && SystemUsesLight());
        IsDark = !light;
        var dict = new ResourceDictionary { Source = new Uri($"pack://application:,,,/UI/Theme.{(light ? "Light" : "Dark")}.xaml") };
        app.Resources.MergedDictionaries[0] = dict;

        Color accent;
        try { accent = (Color)ColorConverter.ConvertFromString(s.AccentHex); } catch { accent = Color.FromRgb(0x6C, 0x8C, 0xFF); }
        app.Resources["AccentBrush"] = new SolidColorBrush(accent);
        app.Resources["AccentHoverBrush"] = new SolidColorBrush(Blend(accent, Colors.White, 0.15));
        app.Resources["AccentSoftBrush"] = new SolidColorBrush(Color.FromArgb(light ? (byte)0x1F : (byte)0x2E, accent.R, accent.G, accent.B));
        app.Resources["AccentSoftStrongBrush"] = new SolidColorBrush(Color.FromArgb(0x55, accent.R, accent.G, accent.B));
        // readable text on top of the accent colour
        double lum = (0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B) / 255;
        app.Resources["AccentTextBrush"] = new SolidColorBrush(lum > 0.62 ? Color.FromRgb(0x10, 0x14, 0x1C) : Colors.White);
        // accent colour tuned for use as text on the card background
        app.Resources["AccentTextOnSurface"] = new SolidColorBrush(light ? Blend(accent, Colors.Black, 0.25) : Blend(accent, Colors.White, 0.12));

        bool compact = s.Density == "Compact";
        // Compact keeps every piece of information, just on less space: smaller avatars and gaps, tighter text, narrower cards (so more columns fit)
        app.Resources["CardPad"] = compact ? new Thickness(11, 8, 11, 8) : new Thickness(18, 16, 18, 16);
        app.Resources["PagePad"] = compact ? new Thickness(24, 18, 24, 18) : new Thickness(32, 26, 32, 26);
        app.Resources["RowPad"] = compact ? new Thickness(10, 4, 10, 4) : new Thickness(16, 12, 16, 12);
        app.Resources["PanelGap"] = compact ? 7.0 : 14.0;
        app.Resources["AvatarSize"] = compact ? 32.0 : 46.0;
        app.Resources["RowAvatarSize"] = compact ? 28.0 : 40.0;
        app.Resources["GridMinWidth"] = compact ? 268.0 : 300.0;
        app.Resources["WideMinWidth"] = compact ? 330.0 : 380.0;
        app.Resources["CardTitleSize"] = compact ? 13.0 : 15.0;
        app.Resources["CardTextSize"] = compact ? 11.5 : 12.5;
        app.Resources["DescHeight"] = compact ? 30.0 : 36.0;
        app.Resources["BtnHeight"] = compact ? 26.0 : 34.0;
        app.Resources["PillPad"] = compact ? new Thickness(7, 1, 7, 1) : new Thickness(9, 3, 9, 3);
        app.Resources["PillFont"] = compact ? 10.5 : 11.0;
        app.Resources["DescMargin"] = compact ? new Thickness(0, 5, 0, 5) : new Thickness(0, 12, 0, 12);
        app.Resources["ActionMargin"] = compact ? new Thickness(0, 6, 0, 0) : new Thickness(0, 14, 0, 0);
        app.Resources["StatMargin"] = compact ? new Thickness(0, 6, 0, 0) : new Thickness(0, 12, 0, 0);

        foreach (Window w in app.Windows) ApplyTitleBar(w);
    }

    static Color Blend(Color a, Color b, double t) => Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    public static void ApplyTitleBar(Window w)
    {
        try
        {
            var h = new WindowInteropHelper(w).Handle;
            if (h == IntPtr.Zero) return;
            int dark = IsDark ? 1 : 0;
            DwmSetWindowAttribute(h, 20, ref dark, 4);
            if (Application.Current.Resources["SurfaceBrush"] is SolidColorBrush b)
            {
                int bgr = b.Color.R | (b.Color.G << 8) | (b.Color.B << 16);
                DwmSetWindowAttribute(h, 35, ref bgr, 4);   // caption colour (Windows 11)
                DwmSetWindowAttribute(h, 34, ref bgr, 4);   // border colour
            }
        }
        catch { }
    }
}

public static class Shell
{
    public static void Open(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }

    public static void Reveal(string path)
    {
        try
        {
            if (System.IO.File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else { System.IO.Directory.CreateDirectory(path); Process.Start("explorer.exe", $"\"{path}\""); }
        }
        catch { }
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static void SetStartWithWindows(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (k == null) return;
            if (on) k.SetValue("ReleaseSync", $"\"{Environment.ProcessPath}\" --background");
            else k.DeleteValue("ReleaseSync", false);
        }
        catch { }
    }
}
