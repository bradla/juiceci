using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tessera.Core;

namespace Tessera.App.ViewModels;

public partial class TableNodeViewModel : ObservableObject
{
    /// <param name="model">Supplies display folders, hierarchies and which tables are hidden.</param>
    /// <param name="showHidden">Include hidden columns, like Power BI's "View hidden".</param>
    public TableNodeViewModel(TableInfo info, Action<FieldNodeViewModel, bool> toggled, SemanticModel? model = null, bool showHidden = false)
    {
        Info = info;
        model ??= new SemanticModel();
        Fields = info.Fields.Select(f => new FieldNodeViewModel(f, toggled)).ToList();
        IsHiddenTable = model.HiddenTables.Contains(info.Name, StringComparer.OrdinalIgnoreCase);

        // The tree: fields and folders side by side in name order, as in Power BI's Data pane.
        var root = new FolderNodeViewModel("", isHierarchy: false);
        foreach (var f in Fields.Where(f => showHidden || !f.Info.IsHidden))
            Folder(root, f.Info.DisplayFolder).Children.Add(f);
        var levels = new List<FieldNodeViewModel>();
        foreach (var h in model.Hierarchies.Where(h => string.Equals(h.Table, info.Name, StringComparison.OrdinalIgnoreCase)))
        {
            var node = new FolderNodeViewModel(h.Name, isHierarchy: true);
            foreach (var level in h.Levels)
            {
                if (info.Fields.FirstOrDefault(f => !f.IsMeasure && f.Name == level) is not { } field) continue;
                var levelNode = new FieldNodeViewModel(field, toggled);
                levels.Add(levelNode);
                node.Children.Add(levelNode);
            }
            if (node.Children.Count > 0) Folder(root, h.DisplayFolder).Children.Add(node);
        }
        root.Sort();
        Items = root.Children;
        AllFields = [.. Fields, .. levels];
    }

    static FolderNodeViewModel Folder(FolderNodeViewModel root, string? path)
    {
        var folder = root;
        foreach (var part in (path ?? "").Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var next = folder.Children.OfType<FolderNodeViewModel>().FirstOrDefault(c => !c.IsHierarchy && c.Name == part);
            if (next is null) folder.Children.Add(next = new FolderNodeViewModel(part, isHierarchy: false));
            folder = next;
        }
        return folder;
    }

    public TableInfo Info { get; }
    public string Name => Info.Name;
    public string RowCountText => $"{Info.RowCount:N0} rows";

    /// <summary>One node per column and measure.</summary>
    public IReadOnlyList<FieldNodeViewModel> Fields { get; }

    /// <summary>Every field node in the tree, including the copies under hierarchies.</summary>
    public IReadOnlyList<FieldNodeViewModel> AllFields { get; }

    /// <summary>Top level of the Data pane tree: fields and folders.</summary>
    public IReadOnlyList<object> Items { get; }

    public bool IsHiddenTable { get; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>False when the Data pane's search, or the table being hidden, keeps it out of the pane.</summary>
    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;

    /// <summary>Tables that only hold measures, which Power BI shows with a calculator icon.</summary>
    public bool IsMeasureTable => Info.Fields.Count > 0 && Info.Fields.All(f => f.IsMeasure);

    /// <summary>True while this table is shown in the table view.</summary>
    [ObservableProperty]
    public partial bool IsPreviewed { get; set; }

    /// <summary>Shows only fields matching the search; returns whether anything in the table matches.</summary>
    public bool ApplySearch(string query)
    {
        var tableMatch = query.Length == 0 || Name.Contains(query, StringComparison.CurrentCultureIgnoreCase);
        foreach (var f in AllFields) f.IsVisible = tableMatch || f.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase);
        foreach (var folder in Items.OfType<FolderNodeViewModel>()) folder.UpdateVisibility(query.Length > 0 && !tableMatch);
        return tableMatch || Items.Any(i => i is FieldNodeViewModel { IsVisible: true } or FolderNodeViewModel { IsVisible: true });
    }
}

/// <summary>A display folder or a hierarchy in the Data pane.</summary>
public partial class FolderNodeViewModel(string name, bool isHierarchy) : ObservableObject
{
    public string Name { get; } = name;
    public bool IsHierarchy { get; } = isHierarchy;
    public List<object> Children { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;

    static string NameOf(object node) => node switch
    {
        FolderNodeViewModel f => f.Name,
        FieldNodeViewModel f => f.Name,
        _ => "",
    };

    internal void Sort()
    {
        // Hierarchy levels keep their drill order.
        if (!IsHierarchy) Children.Sort((a, b) => string.Compare(NameOf(a), NameOf(b), StringComparison.CurrentCultureIgnoreCase));
        foreach (var f in Children.OfType<FolderNodeViewModel>()) f.Sort();
    }

    /// <summary>Visible when a child is; a search opens folders with matches.</summary>
    internal bool UpdateVisibility(bool expandMatches)
    {
        var any = false;
        foreach (var c in Children)
        {
            any |= c switch
            {
                FolderNodeViewModel f => f.UpdateVisibility(expandMatches),
                FieldNodeViewModel f => f.IsVisible,
                _ => false,
            };
        }
        IsVisible = any;
        if (expandMatches && any) IsExpanded = true;
        return any;
    }
}

public partial class FieldNodeViewModel(FieldInfo info, Action<FieldNodeViewModel, bool> toggled) : ObservableObject
{
    public FieldInfo Info { get; } = info;
    public string Name => Info.Name;

    public string Glyph => Info.IsMeasure ? "ƒ" : Info.Kind switch
    {
        FieldKind.Number => "Σ",
        FieldKind.Date => "▦",
        FieldKind.Boolean => "◐",
        _ => "",
    };

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;

    public bool IsMeasure => Info.IsMeasure;

    partial void OnIsCheckedChanged(bool value) => toggled(this, value);
}

public sealed record AggregationOption(Aggregation Value, string Label);

public sealed record GrainOption(DateGrain Value, string Label);

public partial class WellFieldViewModel : ObservableObject
{
    readonly VisualViewModel _owner;

    public WellFieldViewModel(VisualViewModel owner, FieldRef field, bool canSummarize)
    {
        _owner = owner;
        Field = field;
        Options = canSummarize && !field.IsMeasure
            ? Aggregations.AllowedFor(field.Kind)
                .Where(a => a != Aggregation.None || owner.Type == VisualType.Table)
                .Select(a => new AggregationOption(a, Aggregations.Label(a))).ToList()
            : [];
        _selected = Options.FirstOrDefault(o => o.Value == field.Aggregation);
        GrainOptions = !canSummarize && field.Kind == FieldKind.Date
            ? Enum.GetValues<DateGrain>().Select(g => new GrainOption(g, g == DateGrain.None ? "Exact value" : g.ToString())).ToList()
            : [];
        _selectedGrain = GrainOptions.FirstOrDefault(o => o.Value == field.Grain);
    }

    public IReadOnlyList<GrainOption> GrainOptions { get; }
    public bool HasGrain => GrainOptions.Count > 0;

    GrainOption? _selectedGrain;

    public GrainOption? SelectedGrain
    {
        get => _selectedGrain;
        set
        {
            if (value is null || value == _selectedGrain) return;
            _selectedGrain = value;
            Field.Grain = value.Value;
            OnPropertyChanged();
            _owner.FieldOptionChanged();
        }
    }

    public FieldRef Field { get; }
    public string Name => Field.Column;
    public IReadOnlyList<AggregationOption> Options { get; }
    public bool CanSummarize => Options.Count > 0;

    AggregationOption? _selected;

    public AggregationOption? SelectedOption
    {
        get => _selected;
        set
        {
            if (value is null || value == _selected) return;
            _selected = value;
            Field.Aggregation = value.Value;
            OnPropertyChanged();
            _owner.FieldOptionChanged();
        }
    }

    [RelayCommand]
    void Remove() => _owner.RemoveRef(Field);
}
