using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Tessera.Core;

namespace Tessera.App.ViewModels;

/// <summary>A field in the Build pane's "Add drill-through fields here" well.</summary>
public sealed partial class DrillFieldViewModel(FieldRef field, Action<DrillFieldViewModel> remove)
{
    public FieldRef Field { get; } = field;
    public string Name => Field.DisplayName;

    [RelayCommand]
    void Remove() => remove(this);
}

/// <summary>A page another visual can drill through to, with the values it offers.</summary>
public sealed record DrillTarget(string Page, FieldRef Field, IReadOnlyList<string?> Values);

/// <summary>Drill through: a page with drill-through fields opens from other visuals, filtered to one value.</summary>
public partial class ReportViewModel
{
    public ObservableCollection<DrillFieldViewModel> DrillThroughFields { get; } = [];

    void RebuildDrillThrough()
    {
        DrillThroughFields.Clear();
        foreach (var f in _page.DrillThrough ?? []) DrillThroughFields.Add(new DrillFieldViewModel(f, RemoveDrillThroughField));
        OnPropertyChanged(nameof(KeepAllFilters));
        OnPropertyChanged(nameof(CrossReportDrillthrough));
    }

    /// <summary>Makes the current page a drill-through target for this column.</summary>
    public void AddDrillThroughField(FieldInfo field)
    {
        if (field.IsMeasure || (_page.DrillThrough ?? []).Any(f => f.Refers(field))) return;
        var reference = new FieldRef(field, Aggregation.None);
        (_page.DrillThrough ??= []).Add(reference);
        DrillThroughFields.Add(new DrillFieldViewModel(reference, RemoveDrillThroughField));
        RaiseChanged();
    }

    void RemoveDrillThroughField(DrillFieldViewModel field)
    {
        _page.DrillThrough?.Remove(field.Field);
        if (_page.DrillThrough is { Count: 0 }) _page.DrillThrough = null;
        DrillThroughFields.Remove(field);
        RaiseChanged();
    }

    /// <summary>Drill through › Keep all filters: the source page's filters come along.</summary>
    public bool KeepAllFilters
    {
        get => _page.KeepAllFilters;
        set
        {
            if (_page.KeepAllFilters == value) return;
            _page.KeepAllFilters = value;
            OnPropertyChanged();
            RaiseChanged();
        }
    }

    /// <summary>The Filters pane's eye: whether report readers see the pane. Saved with the report.</summary>
    public bool HideFilterPaneForReaders
    {
        get => OptionsCatalog.Flag(_doc.Settings, Core.FileSettings.HideFilterPaneForReaders);
        set
        {
            if (HideFilterPaneForReaders == value) return;
            (_doc.Settings ??= [])[Core.FileSettings.HideFilterPaneForReaders] = value ? "true" : "false";
            OnPropertyChanged();
            RaiseChanged();
        }
    }

    /// <summary>Drill through › Cross-report: the same setting as Options › Report settings.</summary>
    public bool CrossReportDrillthrough
    {
        get => OptionsCatalog.Flag(_doc.Settings, Core.FileSettings.CrossReportDrillthrough);
        set
        {
            if (CrossReportDrillthrough == value) return;
            (_doc.Settings ??= [])[Core.FileSettings.CrossReportDrillthrough] = value ? "true" : "false";
            OnPropertyChanged();
            RaiseChanged();
        }
    }

    /// <summary>
    /// Pages this visual can drill through to: other pages whose drill-through field is one the visual
    /// groups by, with the values it currently shows.
    /// </summary>
    public IReadOnlyList<DrillTarget> DrillTargets(VisualViewModel visual)
    {
        if (visual.Result is not { } result) return [];
        var targets = new List<DrillTarget>();
        foreach (var page in Pages.Select(p => p.Page).Where(p => p != _page && p.DrillThrough is { Count: > 0 }))
            foreach (var field in page.DrillThrough!)
            {
                var column = visual.Spec.Category.FindIndex(c =>
                    c.Table == field.Table && c.Column == field.Column && c.Grain == DateGrain.None);
                if (column < 0 || column >= result.CategoryCount) continue;
                var values = result.Rows.Select(r => Sql.FilterText(r[column])).Distinct().Take(25).ToList();
                targets.Add(new DrillTarget(page.Name, field, values));
            }
        return targets;
    }

    /// <summary>Opens the target page filtered to the value, bringing this page's filters if it keeps them.</summary>
    public void DrillThrough(DrillTarget target, string? value)
    {
        if (Pages.FirstOrDefault(p => p.Name == target.Page) is not { } tab) return;
        var source = _page;
        var page = tab.Page;
        bool Same(FieldFilter f, FieldRef r) => f.Field.Table == r.Table && f.Field.Column == r.Column;

        if (page.KeepAllFilters)
            foreach (var f in Clone(source.Filters).Where(f => f.IsActive))
            {
                page.Filters.RemoveAll(existing => Same(existing, f.Field));
                page.Filters.Add(f);
            }
        page.Filters.RemoveAll(f => Same(f, target.Field));
        page.Filters.Add(new FieldFilter { Field = new FieldRef
        {
            Table = target.Field.Table, Column = target.Field.Column, Kind = target.Field.Kind, Aggregation = Aggregation.None,
        }, Values = [value] });
        ShowPage(tab);
        RaiseChanged();
    }
}
