using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ReleaseSync.Core;
using ReleaseSync.UI;

namespace ReleaseSync.Views;

public partial class LibraryView : UserControl
{
    Point dragStart;
    bool dragging;

    public LibraryView() => InitializeComponent();

    /// <summary>Puts the filter/sort/view controls beside the chips when there's room, or on their own row when there isn't.</summary>
    void Toolbar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Toolbar.ActualWidth < 10) return;
        Chips.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        ToolbarControls.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        bool stack = Chips.DesiredSize.Width + ToolbarControls.DesiredSize.Width + 28 > Toolbar.ActualWidth;
        if (stack)
        {
            Grid.SetRow(ToolbarControls, 1); Grid.SetColumn(ToolbarControls, 0); Grid.SetColumnSpan(ToolbarControls, 2);
            ToolbarControls.HorizontalAlignment = HorizontalAlignment.Left; ToolbarControls.Margin = new Thickness(0, 12, 0, 0);
        }
        else
        {
            Grid.SetRow(ToolbarControls, 0); Grid.SetColumn(ToolbarControls, 1); Grid.SetColumnSpan(ToolbarControls, 1);
            ToolbarControls.HorizontalAlignment = HorizontalAlignment.Right; ToolbarControls.Margin = new Thickness(0);
        }
    }

    void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (dragging) { dragging = false; return; }
        if (sender is FrameworkElement fe && fe.DataContext is TrackedApp a) App.VM.OpenCommand.Execute(a);
    }

    void Card_Down(object sender, MouseButtonEventArgs e) { dragStart = e.GetPosition(null); dragging = false; }

    void Card_Move(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || dragging) return;
        var d = e.GetPosition(null) - dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance * 2 && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance * 2) return;
        if (sender is FrameworkElement fe && fe.DataContext is TrackedApp a)
        {
            dragging = true;
            DragDrop.DoDragDrop(fe, new DataObject("ReleaseSyncApp", a), DragDropEffects.Move);
        }
    }

    /// <summary>The app a card menu was opened for: the menu's placement target is always the card (or its "…" button).</summary>
    static TrackedApp? AppOf(DependencyObject item)
    {
        DependencyObject? d = item;
        while (d != null && d is not System.Windows.Controls.ContextMenu) d = ItemsControl.ItemsControlFromItemContainer(d) ?? LogicalTreeHelper.GetParent(d) ?? (d is FrameworkElement fe ? fe.Parent ?? fe.TemplatedParent : null);
        return d is System.Windows.Controls.ContextMenu cm ? (cm.PlacementTarget as FrameworkElement)?.DataContext as TrackedApp ?? cm.DataContext as TrackedApp : null;
    }

    /// <summary>Ticks the group the app is in when "Move to group" opens.</summary>
    void MoveMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource != sender || sender is not MenuItem parent || AppOf(parent) is not { } app) return;
        var current = app.Group ?? "";
        // the item containers are only created once the popup has opened
        Dispatcher.BeginInvoke(new Action(() =>
        {
            foreach (var choice in parent.Items.OfType<Choice>())
                if (parent.ItemContainerGenerator.ContainerFromItem(choice) is MenuItem mi) mi.IsChecked = (choice.Value as string ?? "") == current;
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    void MoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.DataContext is not Choice choice || AppOf(mi) is not { } app) return;
        var value = choice.Value as string ?? "";
        if (value == MainViewModel.NewGroupMarker) App.VM.NewGroupCommand.Execute(app);
        else App.VM.MoveToGroup(app, value);
        e.Handled = true;
    }
    void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is TrackedApp)
        {
            var menu = (ContextMenu)FindResource("AppMenu");
            menu.DataContext = fe.DataContext;
            menu.PlacementTarget = fe;
            RoutedEventHandler? closed = null;
            closed = (_, _) => { menu.Closed -= closed; menu.ClearValue(DataContextProperty); };      // the menu is shared by every card, so don't leave it pointing at this one
            menu.Closed += closed;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
        e.Handled = true;
    }
    // ---------- sections: fold / open ----------
    void Hdr_Loaded(object sender, RoutedEventArgs e) { if (sender is System.Windows.Controls.Primitives.ToggleButton t) GroupFold.HeaderLoaded("library", t); }
    void Hdr_Click(object sender, RoutedEventArgs e) { if (sender is System.Windows.Controls.Primitives.ToggleButton t) GroupFold.HeaderClicked("library", t); }
    void FoldAll_Click(object sender, RoutedEventArgs e) => GroupFold.SetAll("library", Items, false);
    void OpenAll_Click(object sender, RoutedEventArgs e) => GroupFold.SetAll("library", Items, true);

    // ---------- groups: drop an app on a chip or a section to move it ----------
    static void Glow(object sender, bool on) { if (sender is UIElement u) u.Opacity = on ? 0.55 : 1; }
    void GroupChip_DragEnter(object sender, DragEventArgs e) { if (e.Data.GetDataPresent("ReleaseSyncApp")) { e.Effects = DragDropEffects.Move; Glow(sender, true); } }
    void GroupChip_DragLeave(object sender, DragEventArgs e) => Glow(sender, false);
    void GroupChip_Drop(object sender, DragEventArgs e)
    {
        Glow(sender, false);
        if (sender is FrameworkElement fe && fe.DataContext is ReleaseSync.UI.GroupEntry g && e.Data.GetData("ReleaseSyncApp") is TrackedApp app) App.VM.MoveToGroup(app, g.Name);
    }

    void Section_DragEnter(object sender, DragEventArgs e) { if (e.Data.GetDataPresent("ReleaseSyncApp")) { e.Effects = DragDropEffects.Move; Glow(sender, true); } }
    void Section_DragLeave(object sender, DragEventArgs e) => Glow(sender, false);
    void Section_Drop(object sender, DragEventArgs e)
    {
        Glow(sender, false);
        if (sender is FrameworkElement fe && fe.DataContext is System.Windows.Data.CollectionViewGroup grp && grp.Name is string name && e.Data.GetData("ReleaseSyncApp") is TrackedApp app)
            App.VM.MoveToGroup(app, name);
    }

    /// <summary>Right-click a section title to rename or delete one of your own groups.</summary>
    void Hdr_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not System.Windows.Data.CollectionViewGroup grp || grp.Name is not string name) { e.Handled = true; return; }
        var entry = App.VM.Groups.FirstOrDefault(x => x.Name == name);
        if (entry == null || !entry.IsCustom) { e.Handled = true; return; }
        var menu = new ContextMenu();
        var rename = new MenuItem { Header = "Rename" }; rename.Click += (_, _) => App.VM.RenameGroupCommand.Execute(entry);
        var delete = new MenuItem { Header = "Delete group" }; delete.Click += (_, _) => App.VM.DeleteGroupCommand.Execute(entry);
        menu.Items.Add(rename); menu.Items.Add(delete);
        fe.ContextMenu = menu;
    }
}
