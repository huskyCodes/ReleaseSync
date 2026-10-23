using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using System.Windows.Input;
using ReleaseSync.Core;

namespace ReleaseSync.UI;

/// <summary>One "type" filter button on the Try later page: a kind of project and how many saved ones are of that kind.</summary>
public sealed class TryTypeChip : Observable
{
    public string Name { get; init; } = "";           // empty = everything
    int _count; public int Count { get => _count; set { if (Set(ref _count, value)) Raise(nameof(Label)); } }
    bool _selected; public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
    public string Label => (Name.Length == 0 ? "All" : Name) + "  " + _count;
}

public partial class MainViewModel
{
    public ListCollectionView TryLaterList { get; private set; } = null!;
    public ObservableCollection<TryTypeChip> TryTypes { get; } = new();

    public static readonly List<Choice> TrySortChoices = new()
    {
        new("Recently saved", "recent"), new("Oldest first", "oldest"), new("Name A–Z", "name"), new("Most stars", "stars"), new("Type", "type")
    };

    string _tryText = "";
    public string TryText { get => _tryText; set { if (Set(ref _tryText, value)) RefreshTry(); } }
    string _tryType = "";
    public ICommand SetTryTypeCommand { get; private set; } = null!;
    public ICommand ClearTryFiltersCommand { get; private set; } = null!;
    public ICommand ToggleTryGroupCommand { get; private set; } = null!;
    public ICommand SetTryViewModeCommand { get; private set; } = null!;

    public bool HasTryFilter => _tryText.Trim().Length > 0 || _tryType.Length > 0;
    public bool NoTryMatches => TryLater.Count > 0 && TryLaterList.Count == 0;
    public string TryShownText => TryLater.Count == 0 ? "" : HasTryFilter ? $"Showing {TryLaterList.Count} of {TryLater.Count}" : $"{TryLater.Count} saved";

    void InitTryLater()
    {
        TryLaterList = new ListCollectionView(TryLater) { Filter = TryFilter };
        SetTryTypeCommand = new RelayCommand(p => { _tryType = p as string ?? ""; RefreshTry(); });
        SetTryViewModeCommand = new RelayCommand(p => Settings.TryViewMode = p?.ToString() ?? "Grid");
        ToggleTryGroupCommand = new RelayCommand(() => Settings.TryGroup = !Settings.TryGroup);
        ClearTryFiltersCommand = new RelayCommand(() => { _tryType = ""; TryText = ""; RefreshTry(); });
        TryLater.CollectionChanged += (_, _) => RefreshTry();
        ApplyTryOrder();
        RefreshTry();
    }

    bool TryFilter(object o)
    {
        if (o is not TryLaterItem t) return false;
        if (_tryType.Length > 0 && t.Category != _tryType) return false;
        return TryTextMatches(t);
    }

    bool TryTextMatches(TryLaterItem t)
    {
        var q = _tryText.Trim();
        if (q.Length == 0) return true;
        return t.FullName.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || (t.Description?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false)
            || (t.Note?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false)
            || (t.Language?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false)
            || t.Category.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || (t.Topics?.Any(x => x.Contains(q, StringComparison.CurrentCultureIgnoreCase)) ?? false);
    }

    /// <summary>Applies the chosen sort and, when on, groups the list by type.</summary>
    void ApplyTryOrder()
    {
        using (TryLaterList.DeferRefresh())
        {
            TryLaterList.GroupDescriptions.Clear();
            TryLaterList.SortDescriptions.Clear();
            bool group = Settings.TryGroup;
            if (group) { TryLaterList.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TryLaterItem.Category))); TryLaterList.SortDescriptions.Add(new SortDescription(nameof(TryLaterItem.Category), ListSortDirection.Ascending)); }
            switch (Settings.TrySort)
            {
                case "oldest": TryLaterList.SortDescriptions.Add(new SortDescription(nameof(TryLaterItem.SavedAt), ListSortDirection.Ascending)); break;
                case "name": TryLaterList.SortDescriptions.Add(new SortDescription(nameof(TryLaterItem.Repo), ListSortDirection.Ascending)); break;
                case "stars": TryLaterList.SortDescriptions.Add(new SortDescription(nameof(TryLaterItem.Stars), ListSortDirection.Descending)); break;
                case "type":
                    if (!group) TryLaterList.SortDescriptions.Add(new SortDescription(nameof(TryLaterItem.Category), ListSortDirection.Ascending));
                    TryLaterList.SortDescriptions.Add(new SortDescription(nameof(TryLaterItem.Repo), ListSortDirection.Ascending));
                    break;
                default: TryLaterList.SortDescriptions.Add(new SortDescription(nameof(TryLaterItem.SavedAt), ListSortDirection.Descending)); break;
            }
        }
    }

    /// <summary>Re-filters the list and rebuilds the type buttons (counts follow the search box, so they always add up to what you'd see).</summary>
    void RefreshTry()
    {
        if (TryLaterList == null) return;
        var byType = TryLater.Where(TryTextMatches).GroupBy(t => t.Category).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (_tryType.Length > 0 && !byType.Any(g => g.Key == _tryType) && !TryLater.Any(t => t.Category == _tryType)) _tryType = "";
        var wanted = new List<(string Name, int Count)> { ("", byType.Sum(g => g.Count())) };
        wanted.AddRange(byType.Select(g => (g.Key, g.Count())));
        if (_tryType.Length > 0 && !wanted.Any(w => w.Name == _tryType)) wanted.Add((_tryType, 0));
        for (int i = 0; i < wanted.Count; i++)
        {
            var w = wanted[i];
            if (i < TryTypes.Count && TryTypes[i].Name == w.Name) { TryTypes[i].Count = w.Count; }
            else TryTypes.Insert(i, new TryTypeChip { Name = w.Name, Count = w.Count });
        }
        while (TryTypes.Count > wanted.Count) TryTypes.RemoveAt(TryTypes.Count - 1);
        foreach (var chip in TryTypes) chip.IsSelected = chip.Name == _tryType;
        TryLaterList.Refresh();
        Raise(nameof(HasTryFilter)); Raise(nameof(NoTryMatches)); Raise(nameof(TryShownText));
    }
}
