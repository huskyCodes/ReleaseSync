using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ReleaseSync.Core;
using ReleaseSync.UI;
using Forms = System.Windows.Forms;

namespace ReleaseSync;

public partial class MainWindow : Window
{
    readonly MainViewModel vm;
    Forms.NotifyIcon? tray;
    bool exiting;
    bool shownTrayHint;
    bool drawerVisible;
    double lastWidth;

    public MainWindow(MainViewModel vm)
    {
        this.vm = vm;
        DataContext = vm;
        InitializeComponent();
        Title = "ReleaseSync";
        RestoreBounds_();
        ApplyScale();

        vm.PropertyChanged += OnVmChanged;
        vm.SettingChanged += OnSettingChanged;
        vm.NotifyRequest = (title, text) => ShowBalloon(title, text);
        vm.ExitRequest = ExitApp;
        SizeChanged += (_, _) => UpdateResponsive();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        Loaded += (_, _) => { UpdateResponsive(); SyncTray(); };
        PreviewMouseWheel += OnWheel;
        PreviewKeyDown += OnKey;
        Closing += OnClosing;
        vm.IsCompact = vm.Settings.SidebarCollapsed;
        ApplySidebar();
    }

    // ---------- startup / tray ----------
    public void StartHidden()
    {
        SyncTray();
        if (tray != null && tray.Visible)
        {
            // create the window handle without showing it so theming applies later
            new WindowInteropHelper(this).EnsureHandle();
        }
        else { WindowState = WindowState.Minimized; Show(); }
    }

    public void BringToFront()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate(); Topmost = true; Topmost = false; Focus();
        // Windows only lets the foreground app take focus; if it declined, the user would just see a flashing taskbar button
        var h = new WindowInteropHelper(this).Handle;
        if (h != IntPtr.Zero) SetForegroundWindow(h);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);

    void SyncTray()
    {
        bool want = vm.Settings.ShowTrayIcon;
        if (want && tray == null)
        {
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Open ReleaseSync", null, (_, _) => Dispatcher.Invoke(BringToFront));
            menu.Items.Add("Check for updates", null, (_, _) => Dispatcher.Invoke(() => vm.CheckAllCommand.Execute(null)));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitApp));
            System.Drawing.Icon? icon = null;
            try { icon = new System.Drawing.Icon(Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))!.Stream); } catch { }
            tray = new Forms.NotifyIcon { Icon = icon ?? System.Drawing.SystemIcons.Application, Text = "ReleaseSync", ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += (_, _) => Dispatcher.Invoke(BringToFront);
            tray.BalloonTipClicked += (_, _) => Dispatcher.Invoke(() => { BringToFront(); vm.Navigate("Updates"); });
        }
        else if (!want && tray != null)
        {
            tray.Visible = false; tray.Dispose(); tray = null;
        }
    }

    void ShowBalloon(string title, string text)
    {
        if (tray == null) { if (!IsVisible || WindowState == WindowState.Minimized) { } return; }
        try { tray.ShowBalloonTip(6000, title, text, Forms.ToolTipIcon.Info); } catch { }
    }

    void ExitApp()
    {
        exiting = true;
        SaveBounds();
        vm.SaveAll();
        if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
        Application.Current.Shutdown();
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!exiting && vm.Settings.CloseToTray && vm.Settings.ShowTrayIcon && tray != null)
        {
            e.Cancel = true;
            SaveBounds();
            Hide();
            if (!shownTrayHint) { shownTrayHint = true; ShowBalloon("ReleaseSync is still running", "It lives in the system tray and keeps checking for updates. Right-click the icon to exit."); }
            return;
        }
        if (!exiting) ExitApp();
    }

    // ---------- bounds ----------
    void RestoreBounds_()
    {
        var s = vm.Settings;
        Width = Math.Max(MinWidth, s.WindowWidth); Height = Math.Max(MinHeight, s.WindowHeight);
        if (!double.IsNaN(s.WindowLeft) && !double.IsNaN(s.WindowTop))
        {
            var v = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (v.Contains(new Point(s.WindowLeft + 60, s.WindowTop + 20)))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = s.WindowLeft; Top = s.WindowTop;
            }
        }
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
    }

    void SaveBounds()
    {
        var s = vm.Settings;
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (b.Width < 100 || double.IsNaN(b.Left)) return;
        s.WindowLeft = b.Left; s.WindowTop = b.Top; s.WindowWidth = b.Width; s.WindowHeight = b.Height;
        s.WindowMaximized = WindowState == WindowState.Maximized;
        vm.ScheduleSaveSettings();
    }

    // ---------- responsive layout / scale ----------
    void ApplyScale()
    {
        var sc = Math.Clamp(vm.Settings.UiScale, 0.7, 1.8);
        ScaleT.ScaleX = ScaleT.ScaleY = sc;
    }

    void UpdateResponsive()
    {
        double sc = Math.Max(0.5, ScaleT.ScaleX);
        double w = ActualWidth / sc;
        bool narrow = w < 1180;
        if (vm.IsNarrow != narrow) vm.IsNarrow = narrow;
        bool autoCompact = w < 980;
        bool crossed = (lastWidth / sc < 980) != autoCompact || lastWidth == 0;
        lastWidth = ActualWidth;
        if (crossed) { vm.IsCompact = autoCompact || vm.Settings.SidebarCollapsed; ApplySidebar(); }
        Drawer.Width = Math.Min(440, Math.Max(300, w * 0.94));
        if (drawerVisible) DrawerT.X = 0; else DrawerT.X = Drawer.Width + 20;
    }

    void ApplySidebar() => SidebarCol.Width = new GridLength(vm.IsCompact ? 76 : 252);

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        StepScale(e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    void StepScale(int dir)
    {
        var steps = MainViewModel.ScaleChoices.Select(c => (double)c.Value).ToList();
        int i = steps.FindIndex(x => Math.Abs(x - vm.Settings.UiScale) < 0.001);
        if (i < 0) i = steps.IndexOf(1.0);
        i = Math.Clamp(i + dir, 0, steps.Count - 1);
        vm.Settings.UiScale = steps[i];
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key is Key.OemPlus or Key.Add) { StepScale(1); e.Handled = true; }
            else if (e.Key is Key.OemMinus or Key.Subtract) { StepScale(-1); e.Handled = true; }
            else if (e.Key is Key.D0 or Key.NumPad0) { vm.Settings.UiScale = 1.0; e.Handled = true; }
            else if (e.Key == Key.K || e.Key == Key.L) { Omnibox.Focus(); Omnibox.SelectAll(); e.Handled = true; }
        }
        if (e.Key == Key.Escape)
        {
            if (vm.IsPromptOpen) { vm.Prompt = null; e.Handled = true; }
            else if (vm.IsRepoOpen) { vm.RepoDetail = null; e.Handled = true; }
            else if (vm.IsAddOpen) { vm.AddPreview = null; e.Handled = true; }
        }
    }

    // ---------- vm reactions ----------
    void OnSettingChanged(string name)
    {
        switch (name)
        {
            case nameof(AppSettings.UiScale): ApplyScale(); UpdateResponsive(); break;
            case nameof(AppSettings.ShowTrayIcon): SyncTray(); break;
            case nameof(AppSettings.SidebarCollapsed): break;
            case nameof(AppSettings.ReduceMotion): break;
        }
    }

    void OnVmChanged(object? s, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsDrawerOpen): SetDrawer(vm.IsDrawerOpen); break;
            case nameof(MainViewModel.IsModalOpen): ModalLayer.Visibility = vm.IsModalOpen ? Visibility.Visible : Visibility.Collapsed; break;
            case nameof(MainViewModel.IsCompact): ApplySidebar(); break;
        }
    }

    void SetDrawer(bool open)
    {
        if (open == drawerVisible) return;
        drawerVisible = open;
        bool anim = !vm.Settings.ReduceMotion;
        var dur = TimeSpan.FromMilliseconds(anim ? 220 : 1);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        if (open)
        {
            DrawerLayer.Visibility = Visibility.Visible;
            DrawerT.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(Drawer.Width + 20, 0, dur) { EasingFunction = ease });
            DrawerScrim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur));
        }
        else
        {
            var slide = new DoubleAnimation(DrawerT.X, Drawer.Width + 20, dur) { EasingFunction = ease };
            slide.Completed += (_, _) => { if (!drawerVisible) DrawerLayer.Visibility = Visibility.Collapsed; };
            DrawerT.BeginAnimation(TranslateTransform.XProperty, slide);
            DrawerScrim.BeginAnimation(OpacityProperty, new DoubleAnimation(DrawerScrim.Opacity, 0, dur));
        }
    }

    // ---------- handlers referenced from XAML ----------
    void Scrim_Click(object sender, MouseButtonEventArgs e) => vm.SelectedApp = null;
    void ModalScrim_Click(object sender, MouseButtonEventArgs e) { if (vm.IsPromptOpen) vm.Prompt = null; else if (vm.IsRepoOpen) vm.RepoDetail = null; else if (vm.IsAddOpen) vm.AddPreview = null; }
    void Rate_Click(object sender, MouseButtonEventArgs e) { vm.SettingsSection = "GitHub"; vm.Navigate("Settings"); }
}
