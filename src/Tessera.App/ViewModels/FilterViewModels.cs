using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tessera.Core;

namespace Tessera.App.ViewModels;

/// <summary>Where a filter applies, and the drop target for adding one.</summary>
public enum FilterScope { Visual, Page }

public sealed partial class FilterValueViewModel(string? value, bool isChecked, Action changed) : ObservableObject
{
    public string? Value { get; } = value;
    public string Label => Value ?? "(Blank)";

    [ObservableProperty]
    public partial bool IsChecked { get; set; } = isChecked;

    partial void OnIsCheckedChanged(bool value) => changed();
}

/// <summary>A card in the Filters pane: a field and a checklist of its values (basic filtering).</summary>
public sealed partial class FilterCardViewModel : ObservableObject
{
    readonly Action _changed;
    readonly Action<FilterCardViewModel> _remove;
    bool _syncing;

    public FilterCardViewModel(FieldFilter filter, IReadOnlyList<string?> values, Action changed, Action<FilterCardViewModel> remove)
    {
        Filter = filter;
        _changed = changed;
        _remove = remove;
        // Values the filter keeps that are no longer in the data still show, so they can be unticked.
        foreach (var v in values.Concat(filter.Values.Where(v => !values.Contains(v))))
            Values.Add(new FilterValueViewModel(v, filter.Values.Contains(v), OnValueToggled));
    }

    public FieldFilter Filter { get; }
    public string Name => Filter.Field.Column;
    public ObservableCollection<FilterValueViewModel> Values { get; } = [];

    public string Summary => Filter.Rules is { Count: > 0 } rules && Filter.Values.Count == 0
        ? string.Join(Filter.MatchAny ? " or " : " and ", rules.Select(r => r.Describe()))
        : Filter.Values.Count switch
        {
            0 => "is (All)",
            <= 2 => "is " + string.Join(" or ", Filter.Values.Select(v => v ?? "(Blank)")),
            var n => $"is {n} values",
        };

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>Power BI's "Select all": ticked when nothing is filtered out.</summary>
    public bool SelectAll
    {
        get => !Filter.IsActive;
        set
        {
            if (!value) return;
            Filter.Rules = null;
            _syncing = true;
            foreach (var v in Values) v.IsChecked = false;
            _syncing = false;
            Apply();
        }
    }

    void OnValueToggled()
    {
        if (!_syncing) Apply();
    }

    void Apply()
    {
        Filter.Values = Values.Where(v => v.IsChecked).Select(v => v.Value).ToList();
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(SelectAll));
        _changed();
    }

    [RelayCommand]
    void Clear() => SelectAll = true;

    [RelayCommand]
    void Remove() => _remove(this);
}
