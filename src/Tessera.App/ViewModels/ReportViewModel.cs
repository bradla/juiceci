using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tessera.Core;

namespace Tessera.App.ViewModels;

public enum Well { Category, Values }

/// <summary>How the page is sized on screen: fitted, or at <see cref="ReportViewModel.ZoomPercent"/>.</summary>
public enum PageZoom { FitPage, FitWidth, Custom }

public sealed record VisualTypeOption(VisualType Type, string Label, string IconKey);

/// <summary>
/// A button in the Visualizations pane's gallery of Power BI visual types. <see cref="Type"/> is null for the
/// ones JuiceCI can't draw yet; they show greyed out so the gallery reads like Power BI's.
/// </summary>
public sealed record GalleryItem(string Label, string IconKey, VisualType? Type, string? Glyph = null)
{
    public bool IsAvailable => Type is not null;
    public bool HasGlyph => Glyph is not null;
    public string Tip => IsAvailable ? Label : $"{Label}: not available in JuiceCI yet";
}

/// <summary>A tab in the page bar under the canvas.</summary>
public partial class PageTabViewModel(ReportPage page) : ObservableObject
{
    public ReportPage Page { get; } = page;

    public string Name => Page.Name;

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    /// <summary>The page the report opens on; its tab shows a house.</summary>
    [ObservableProperty]
    public partial bool IsLanding { get; set; }

    internal void Renamed() => OnPropertyChanged(nameof(Name));
}

public partial class ReportViewModel : ObservableObject
{
    readonly DataEngine _engine;
    ReportDocument _doc = new();
    ReportPage _page;
    bool _syncingChecks;
    bool _restoring;

    public ReportViewModel(DataEngine engine)
    {
        _engine = engine;
        _page = _doc.Pages[0];
        Pages.Add(new PageTabViewModel(_page) { IsCurrent = true });
        Visuals.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Layers));
            OnPropertyChanged(nameof(OffMobileVisuals));
            if (IsMobileLayout) OnPropertyChanged(nameof(PageHeight));
        };
        History.Reset(Snapshot());
    }

    public UndoHistory History { get; } = new();

    /// <summary>Raised on any edit that should mark the document unsaved.</summary>
    public event EventHandler? Changed;

    public static IReadOnlyList<VisualTypeOption> VisualTypes { get; } =
    [
        new(VisualType.ColumnChart, "Column chart", "Visual.ColumnChart"),
        new(VisualType.BarChart, "Bar chart", "Visual.BarChart"),
        new(VisualType.LineChart, "Line chart", "Visual.LineChart"),
        new(VisualType.AreaChart, "Area chart", "Visual.AreaChart"),
        new(VisualType.PieChart, "Pie chart", "Visual.PieChart"),
        new(VisualType.DonutChart, "Donut chart", "Visual.Donut"),
        new(VisualType.Table, "Table", "Visual.Table"),
        new(VisualType.Card, "Card", "Visual.Card"),
        new(VisualType.DecompositionTree, "Decomposition tree", "Visual.DecompositionTree"),
        new(VisualType.KeyInfluencers, "Key influencers", "Visual.KeyInfluencers"),
    ];

    /// <summary>Power BI's Visualizations gallery, in its order.</summary>
    public static IReadOnlyList<GalleryItem> FullGallery { get; } =
    [
        new("Stacked bar chart", "Visual.StackedBar", null), new("Stacked column chart", "Visual.StackedColumn", null),
        new("Clustered bar chart", "Visual.BarChart", VisualType.BarChart), new("Clustered column chart", "Visual.ColumnChart", VisualType.ColumnChart),
        new("100% stacked bar chart", "Visual.FullBar", null), new("100% stacked column chart", "Visual.FullColumn", null),
        new("Line chart", "Visual.LineChart", VisualType.LineChart), new("Area chart", "Visual.AreaChart", VisualType.AreaChart),
        new("Stacked area chart", "Visual.StackedArea", null), new("100% stacked area chart", "Visual.FullArea", null),
        new("Line and stacked column chart", "Visual.LineStackedColumn", null),
        new("Line and clustered column chart", "Visual.LineClusteredColumn", null),
        new("Ribbon chart", "Visual.Ribbon", null), new("Waterfall chart", "Visual.Waterfall", null), new("Funnel", "Visual.Funnel", null),
        new("Scatter chart", "Visual.Scatter", null), new("Pie chart", "Visual.PieChart", VisualType.PieChart),
        new("Donut chart", "Visual.Donut", VisualType.DonutChart),
        new("Treemap", "Visual.Treemap", null), new("Map", "Visual.Map", null), new("Filled map", "Visual.FilledMap", null),
        new("Shape map", "Visual.ShapeMap", null), new("Azure map", "Visual.AzureMap", null), new("Gauge", "Visual.Gauge", null),
        new("Card", "Visual.Card", VisualType.Card), new("KPI", "Visual.Kpi", null), new("Slicer", "Visual.Slicer", null),
        new("Table", "Visual.Table", VisualType.Table), new("Matrix", "Visual.Matrix", null),
        new("R script visual", "", null, "R"), new("Python visual", "", null, "Py"),
        new("Key influencers", "Visual.KeyInfluencers", VisualType.KeyInfluencers),
        new("Decomposition tree", "Visual.DecompositionTree", VisualType.DecompositionTree),
        new("Q&A", "Visual.QnA", null), new("Narrative", "Visual.Narrative", null), new("Metrics", "Visual.Metrics", null),
        new("Paginated report", "Visual.Paginated", null), new("Power Apps for Power BI", "Visual.PowerApps", null),
        new("Power Automate for Power BI", "Visual.PowerAutomate", null), new("Get more visuals", "", null, "···"),
    ];

    /// <summary>The gallery less any preview feature turned off.</summary>
    public ObservableCollection<GalleryItem> Gallery { get; } = new(FullGallery);

    /// <summary>The visual types offered for new visuals: <see cref="VisualTypes"/> less any preview feature turned off.</summary>
    public ObservableCollection<VisualTypeOption> AvailableVisualTypes { get; } = new(VisualTypes);

    /// <summary>Insert › AI visuals follow the Preview features page too.</summary>
    public bool HasKeyInfluencers => AvailableVisualTypes.Any(t => t.Type == VisualType.KeyInfluencers);
    public bool HasDecompositionTree => AvailableVisualTypes.Any(t => t.Type == VisualType.DecompositionTree);

    /// <summary>Applies the Preview features page.</summary>
    public void SetPreviewVisuals(bool decompositionTree, bool keyInfluencers)
    {
        var wanted = VisualTypes.Where(t => t.Type switch
        {
            VisualType.DecompositionTree => decompositionTree,
            VisualType.KeyInfluencers => keyInfluencers,
            _ => true,
        }).ToList();
        if (wanted.SequenceEqual(AvailableVisualTypes)) return;
        AvailableVisualTypes.Clear();
        foreach (var t in wanted) AvailableVisualTypes.Add(t);
        OnPropertyChanged(nameof(HasKeyInfluencers));
        OnPropertyChanged(nameof(HasDecompositionTree));
        Gallery.Clear();
        foreach (var g in FullGallery.Where(g => g.Type is null || wanted.Any(t => t.Type == g.Type))) Gallery.Add(g);
    }

    /// <summary>Table view's data preview shows spaces as · and line breaks as ↵ (Power Query Editor options).</summary>
    [ObservableProperty]
    public partial bool ShowWhitespace { get; set; }

    [ObservableProperty]
    public partial bool MonospacedPreview { get; set; }

    /// <summary>The Options dialog's Current file choices, saved with the report.</summary>
    public IReadOnlyDictionary<string, string>? FileSettings => _doc.Settings;

    /// <summary>Replaces the Current file choices; undoable like any other edit.</summary>
    public void SetFileSettings(IReadOnlyDictionary<string, string> values)
    {
        var current = _doc.Settings ?? [];
        if (current.Count == values.Count && values.All(kv => current.TryGetValue(kv.Key, out var v) && v == kv.Value)) return;
        _doc.Settings = values.Count == 0 ? null : new Dictionary<string, string>(values);
        OnFileSettingsChanged();
        RaiseChanged();
    }

    void OnFileSettingsChanged()
    {
        OnPropertyChanged(nameof(FileSettings));
        OnPropertyChanged(nameof(IsFilterSearchEnabled));
        OnPropertyChanged(nameof(CrossReportDrillthrough));
        OnPropertyChanged(nameof(HideFilterPaneForReaders));
        if (!IsFilterSearchEnabled) FilterSearch = "";
    }

    /// <summary>Report settings › Display smart guides when visuals are aligned.</summary>
    public bool SmartGuides { get; set; } = true;

    /// <summary>Report settings › Page alignment: top of the wallpaper, or its centre.</summary>
    [ObservableProperty]
    public partial bool AlignPageTop { get; set; } = true;

    /// <summary>Report settings › Copy object names when right clicking on report objects.</summary>
    public bool CopyObjectNames { get; set; }

    /// <summary>Snap step for placing, moving and resizing visuals; 0 or 1 turns snapping off.</summary>
    public int GridSize { get; set; } = 8;

    /// <summary>How date fields dropped on an axis are grouped at first.</summary>
    public DateGrain DefaultDateGrain { get; set; } = DateGrain.Month;

    public double PageWidth => IsMobileLayout ? MobileWidth : _page.Width;
    public double PageHeight => IsMobileLayout ? MobileHeight : _page.Height;
    public string PageName => _page.Name;
    public string PageBackground => _page.Background ?? Theme.PageBackground ?? "#FFFFFF";

    /// <summary>Format pane with nothing selected: the page's colour.</summary>
    [RelayCommand]
    void SetPageBackground(string? color)
    {
        if (_page.Background == color) return;
        _page.Background = color;
        OnPropertyChanged(nameof(PageBackground));
        RaiseChanged();
    }

    public ObservableCollection<VisualViewModel> Visuals { get; } = [];
    public ObservableCollection<PageTabViewModel> Pages { get; } = [];
    public bool CanDeletePage => Pages.Count > 1;
    public ObservableCollection<TableNodeViewModel> Tables { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial VisualViewModel? Selected { get; set; }

    public bool HasSelection => Selected is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReportView))]
    public partial bool IsDataView { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReportView))]
    public partial bool IsModelView { get; set; }

    public bool IsReportView => !IsDataView && !IsModelView;

    public DataEngine Engine => _engine;

    // ---- Panes ----

    [ObservableProperty]
    public partial bool ShowFiltersPane { get; set; } = true;

    /// <summary>Expanded or collapsed to a narrow strip; it starts expanded, as in Power BI Desktop.</summary>
    [ObservableProperty]
    public partial bool IsFiltersExpanded { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowVisualizationsPane { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowDataPane { get; set; } = true;

    /// <summary>Visualizations pane: Build (fields) or Format (look).</summary>
    [ObservableProperty]
    public partial bool IsFormatTab { get; set; }

    [RelayCommand]
    void ShowBuildTab() => IsFormatTab = false;

    [RelayCommand]
    void ShowFormatTab() => IsFormatTab = true;

    [RelayCommand]
    void ToggleFilters() => IsFiltersExpanded = !IsFiltersExpanded;

    [RelayCommand]
    void OpenVisualizationsPane() => ShowVisualizationsPane = true;

    [RelayCommand]
    void CloseVisualizationsPane() => ShowVisualizationsPane = false;

    [RelayCommand]
    void OpenDataPane() => ShowDataPane = true;

    [RelayCommand]
    void CloseDataPane() => ShowDataPane = false;

    [RelayCommand]
    void ViewTableData(TableNodeViewModel table)
    {
        ShowDataView();
        PreviewTable = table;
    }

    [RelayCommand]
    void AddPageFilter(FieldNodeViewModel field) => AddFilter(field.Info, FilterScope.Page);

    // ---- Measures: the owner shows the editor ----

    /// <summary>Opens the measure editor for a new measure on a table (null: the owner picks), or an existing one.</summary>
    public Func<string?, Measure?, Task>? MeasureEditor { get; set; }

    [RelayCommand]
    Task NewMeasure(string? table) => MeasureEditor?.Invoke(table ?? Selected?.Spec.Values.FirstOrDefault()?.Table, null) ?? Task.CompletedTask;

    [RelayCommand]
    Task EditMeasure(FieldNodeViewModel field) =>
        _engine.Model.FindMeasure(field.Info.Table, field.Name) is { } m && MeasureEditor is not null
            ? MeasureEditor(m.Table, m)
            : Task.CompletedTask;

    [RelayCommand]
    void DeleteMeasure(FieldNodeViewModel field)
    {
        var model = _engine.Model;
        if (model.FindMeasure(field.Info.Table, field.Name) is not { } m) return;
        _engine.Model = model.WithMeasures(model.Measures.Where(x => x != m).ToList());
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---- Zoom ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomText))]
    public partial PageZoom Zoom { get; set; }

    double _zoomPercent = 100;

    /// <summary>The page's on-screen size; setting it switches to a custom zoom.</summary>
    public double ZoomPercent
    {
        get => _zoomPercent;
        set
        {
            value = Math.Clamp(Math.Round(value), 10, 400);
            Zoom = PageZoom.Custom;
            if (SetProperty(ref _zoomPercent, value)) OnPropertyChanged(nameof(ZoomText));
        }
    }

    public string ZoomText => $"{_zoomPercent:0}%";

    /// <summary>Called by the view with the scale a fit mode produced.</summary>
    public void ReportFittedZoom(double percent)
    {
        if (SetProperty(ref _zoomPercent, Math.Round(percent), nameof(ZoomPercent))) OnPropertyChanged(nameof(ZoomText));
    }

    [RelayCommand]
    void SetZoom(PageZoom zoom) => Zoom = zoom;

    [RelayCommand]
    void ZoomIn() => ZoomPercent = _zoomPercent + 10;

    [RelayCommand]
    void ZoomOut() => ZoomPercent = _zoomPercent - 10;

    [RelayCommand]
    void ActualSize() => ZoomPercent = 100;

    public string PageIndicator => $"Page {CurrentPageIndex + 1} of {Pages.Count}";

    // ---- Data pane search ----

    [ObservableProperty]
    public partial string FieldSearch { get; set; } = "";

    partial void OnFieldSearchChanged(string value) => ApplyFieldSearch();

    void ApplyFieldSearch()
    {
        var q = FieldSearch.Trim();
        foreach (var t in Tables)
        {
            var match = t.ApplySearch(q);
            t.IsVisible = match && (ShowHiddenFields || !t.IsHiddenTable);
            if (q.Length > 0 && match && !t.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)) t.IsExpanded = true;
        }
    }

    /// <summary>Show hidden tables and columns in the Data pane, like Power BI's "View hidden".</summary>
    [ObservableProperty]
    public partial bool ShowHiddenFields { get; set; }

    partial void OnShowHiddenFieldsChanged(bool value) => RefreshTables();

    [RelayCommand]
    void CollapseAllTables()
    {
        foreach (var t in Tables) t.IsExpanded = false;
    }

    [RelayCommand]
    void ExpandAllTables()
    {
        foreach (var t in Tables.Where(t => t.IsVisible)) t.IsExpanded = true;
    }

    [ObservableProperty]
    public partial TableNodeViewModel? PreviewTable { get; set; }

    [ObservableProperty]
    public partial QueryResult? PreviewResult { get; set; }

    public bool HasTables => Tables.Count > 0;

    partial void OnSelectedChanged(VisualViewModel? oldValue, VisualViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        if (newValue is not null && newValue != oldValue) PaintFormat(newValue);
        SyncFieldChecks();
        RebuildVisualFilters();
        PasteCommand.NotifyCanExecuteChanged();
    }

    // ---- Filters ----

    public ObservableCollection<FilterCardViewModel> VisualFilters { get; } = [];
    public ObservableCollection<FilterCardViewModel> PageFilters { get; } = [];
    /// <summary>Filters on all pages.</summary>
    public ObservableCollection<FilterCardViewModel> ReportFilters { get; } = [];

    public int ActiveFilterCount => ReportFilters.Concat(PageFilters).Concat(VisualFilters).Count(f => f.Filter.IsActive);

    /// <summary>Report settings › "Enable search for the filter pane".</summary>
    public bool IsFilterSearchEnabled => OptionsCatalog.Flag(_doc.Settings, Core.FileSettings.FilterPaneSearch);

    [ObservableProperty]
    public partial string FilterSearch { get; set; } = "";

    partial void OnFilterSearchChanged(string value) => ApplyFilterSearch();

    void ApplyFilterSearch()
    {
        var q = FilterSearch.Trim();
        foreach (var card in VisualFilters.Concat(PageFilters).Concat(ReportFilters))
            card.IsMatch = q.Length == 0 || card.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase);
    }

    FilterCardViewModel Card(FieldFilter filter, FilterScope scope)
    {
        IReadOnlyList<string?> values;
        var broken = false;
        try
        {
            values = _engine.DistinctValues(filter.Field.Table, filter.Field.Column);
        }
        catch (Exception)
        {
            values = [];   // the field is gone; the card still lets the filter be removed
            broken = true;
        }
        return new FilterCardViewModel(filter, values, () => FilterChanged(scope), card => RemoveFilter(card, scope)) { IsBroken = broken };
    }

    void RebuildVisualFilters()
    {
        VisualFilters.Clear();
        if (Selected is { ShowsData: true } v)
            foreach (var f in v.Spec.Filters) VisualFilters.Add(Card(f, FilterScope.Visual));
        ApplyFilterSearch();
        OnPropertyChanged(nameof(ActiveFilterCount));
    }

    void RebuildPageFilters()
    {
        PageFilters.Clear();
        foreach (var f in _page.Filters) PageFilters.Add(Card(f, FilterScope.Page));
        ReportFilters.Clear();
        foreach (var f in _doc.Filters) ReportFilters.Add(Card(f, FilterScope.Report));
        ApplyFilterSearch();
        OnPropertyChanged(nameof(ActiveFilterCount));
    }

    void FilterChanged(FilterScope scope)
    {
        if (scope != FilterScope.Visual) foreach (var v in Visuals) v.Refresh();
        else Selected?.Refresh();
        OnPropertyChanged(nameof(ActiveFilterCount));
        RaiseChanged();
    }

    /// <summary>Adds a filter card for a column, to the selected visual or the page.</summary>
    public void AddFilter(FieldInfo field, FilterScope scope)
    {
        if (field.IsMeasure) return;
        var list = scope switch
        {
            FilterScope.Report => _doc.Filters,
            FilterScope.Page => _page.Filters,
            _ => Selected is { ShowsData: true } v ? v.Spec.Filters : null,
        };
        if (list is null || list.Any(f => f.Field.Refers(field))) return;
        var filter = new FieldFilter { Field = new FieldRef(field, Aggregation.None) };
        list.Add(filter);
        var card = Card(filter, scope);
        card.IsExpanded = true;
        (scope switch { FilterScope.Report => ReportFilters, FilterScope.Page => PageFilters, _ => VisualFilters }).Add(card);
        IsFiltersExpanded = true;
        RaiseChanged();
    }

    void RemoveFilter(FilterCardViewModel card, FilterScope scope)
    {
        if (scope == FilterScope.Report)
        {
            _doc.Filters.Remove(card.Filter);
            ReportFilters.Remove(card);
        }
        else if (scope == FilterScope.Page)
        {
            _page.Filters.Remove(card.Filter);
            PageFilters.Remove(card);
        }
        else
        {
            Selected?.Spec.Filters.Remove(card.Filter);
            VisualFilters.Remove(card);
        }
        FilterChanged(scope);
    }

    // ---- Clipboard ----

    static string? _clipboard;

    [RelayCommand]
    void Copy()
    {
        if (Selected is null) return;
        _clipboard = JsonSerializer.Serialize(Selected.Spec);
        PasteCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    void Cut()
    {
        Copy();
        DeleteSelected();
    }

    [RelayCommand(CanExecute = nameof(CanPaste))]
    void Paste()
    {
        if (_clipboard is null || JsonSerializer.Deserialize<VisualSpec>(_clipboard) is not { } spec) return;
        spec.Id = Guid.NewGuid().ToString("N");
        // Offset the copy so it's visible, keeping it on the page.
        spec.X = Math.Clamp(spec.X + 24, 0, Math.Max(0, PageWidth - spec.Width));
        spec.Y = Math.Clamp(spec.Y + 24, 0, Math.Max(0, PageHeight - spec.Height));
        var vm = CreateVisual(spec);
        Visuals.Add(vm);
        Selected = vm;
        RaiseChanged();
    }

    bool CanPaste() => _clipboard is not null;

    // ---- Insert ----

    [RelayCommand]
    void InsertTextBox()
    {
        var vm = AddElement(VisualType.Text, 320, 64, new VisualStyle { Text = "Text box", FontSize = 20 });
        IsFormatTab = true;
        Selected = vm;
    }

    [RelayCommand]
    void InsertShape(string? kind)
    {
        var square = kind is "Oval" or "Triangle";
        Selected = AddElement(VisualType.Shape, square ? 160 : 240, square ? 160 : 120,
            new VisualStyle { Background = "#D6E4F0", Shape = kind is null or "Rectangle" ? null : kind });
    }

    /// <summary>Adds an image from a PNG or JPEG file's bytes.</summary>
    public void InsertImage(byte[] data) =>
        Selected = AddElement(VisualType.Image, 200, 160, new VisualStyle { Image = Convert.ToBase64String(data) });

    [RelayCommand]
    void InsertBookmarkNavigator() => Selected = AddElement(VisualType.BookmarkNavigator, Math.Min(480, PageWidth), 36, new VisualStyle());

    [RelayCommand]
    void InsertPageNavigator() => Selected = AddElement(VisualType.PageNavigator, Math.Min(480, PageWidth), 36, new VisualStyle());

    VisualViewModel AddElement(VisualType type, double w, double h, VisualStyle style)
    {
        var spec = new VisualSpec { Type = type, Width = w, Height = h, Style = style };
        (spec.X, spec.Y) = NextFreeSpot(w, h);
        var vm = CreateVisual(spec);
        Visuals.Add(vm);
        RaiseChanged();
        return vm;
    }

    partial void OnIsDataViewChanged(bool value)
    {
        if (value && PreviewTable is null) PreviewTable = Tables.FirstOrDefault();
    }

    partial void OnPreviewTableChanged(TableNodeViewModel? value)
    {
        foreach (var t in Tables) t.IsPreviewed = t == value;
        PreviewResult = value is null ? null : _engine.Preview(value.Name);
    }

    public void Load(ReportDocument doc)
    {
        // A report opens on its landing page, as it does in Power BI.
        Apply(doc, Math.Max(0, doc.Pages.FindIndex(p => p.Name == doc.LandingPage)));
        IsDataView = false;
        ResetHistory();
    }

    void Apply(ReportDocument doc, int pageIndex)
    {
        if (doc.Pages.Count == 0) doc.Pages.Add(new ReportPage());
        _doc = doc;
        OnFileSettingsChanged();
        Pages.Clear();
        foreach (var page in doc.Pages) Pages.Add(new PageTabViewModel(page));
        OnPropertyChanged(nameof(CanDeletePage));
        OnPropertyChanged(nameof(PageIndicator));
        RebuildBookmarks();
        UpdateLandingPage();
        OnPropertyChanged(nameof(Theme));
        RefreshTables();
        ShowPage(Pages[Math.Clamp(pageIndex, 0, Pages.Count - 1)]);
    }

    /// <summary>Puts a page on the canvas. Switching pages isn't an edit, so it isn't undoable.</summary>
    void ShowPage(PageTabViewModel tab)
    {
        if (Pages.FirstOrDefault(p => p.IsCurrent) is { } current) current.Page.Visuals = Visuals.Select(v => v.Spec).ToList();
        foreach (var p in Pages) p.IsCurrent = p == tab;
        _page = tab.Page;
        Selected = null;
        Visuals.Clear();
        foreach (var spec in _page.Visuals) Visuals.Add(CreateVisual(spec));
        RebuildPageFilters();
        RebuildDrillThrough();
        OnPropertyChanged(nameof(PageIndicator));
        OnPropertyChanged(nameof(PageBackground));
        OnPropertyChanged(nameof(PageWallpaper));
        OnPropertyChanged(nameof(PageWidth));
        OnPropertyChanged(nameof(PageHeight));
        OnPropertyChanged(nameof(PageName));
    }

    int CurrentPageIndex => Math.Max(0, Pages.IndexOf(Pages.FirstOrDefault(p => p.IsCurrent)!));

    [RelayCommand]
    void SelectPage(PageTabViewModel tab)
    {
        if (!tab.IsCurrent) ShowPage(tab);
    }

    [RelayCommand]
    void PreviousPage() => ShowPage(Pages[(CurrentPageIndex + Pages.Count - 1) % Pages.Count]);

    [RelayCommand]
    void NextPage() => ShowPage(Pages[(CurrentPageIndex + 1) % Pages.Count]);

    /// <summary>Shows the page with this name, as a page navigator button does.</summary>
    public void SelectPage(string name)
    {
        if (Pages.FirstOrDefault(p => p.Name == name) is { } tab) SelectPage(tab);
    }

    [RelayCommand]
    void AddPage()
    {
        var names = Pages.Select(p => p.Name).ToHashSet();
        var n = Pages.Count + 1;
        while (names.Contains($"Page {n}")) n++;
        var page = new ReportPage { Name = $"Page {n}", Width = _page.Width, Height = _page.Height };
        _doc.Pages.Add(page);
        var tab = new PageTabViewModel(page);
        Pages.Add(tab);
        OnPropertyChanged(nameof(CanDeletePage));
        OnPropertyChanged(nameof(PageIndicator));
        ShowPage(tab);
        RaiseChanged();
    }

    /// <summary>Insert › New page › Duplicate page: a copy of the current page, its visuals and filters.</summary>
    [RelayCommand]
    void DuplicatePage()
    {
        _page.Visuals = Visuals.Select(v => v.Spec).ToList();
        var copy = JsonSerializer.Deserialize<ReportPage>(JsonSerializer.Serialize(_page))!;
        foreach (var v in copy.Visuals) v.Id = Guid.NewGuid().ToString("N");
        var names = Pages.Select(p => p.Name).ToHashSet();
        copy.Name = $"Duplicate of {_page.Name}";
        for (var n = 2; names.Contains(copy.Name); n++) copy.Name = $"Duplicate of {_page.Name} ({n})";
        _doc.Pages.Insert(_doc.Pages.IndexOf(_page) + 1, copy);
        var tab = new PageTabViewModel(copy);
        Pages.Insert(CurrentPageIndex + 1, tab);
        OnPropertyChanged(nameof(CanDeletePage));
        OnPropertyChanged(nameof(PageIndicator));
        ShowPage(tab);
        RaiseChanged();
    }

    [RelayCommand]
    void DeletePage(PageTabViewModel tab)
    {
        if (Pages.Count < 2) return;
        var index = Pages.IndexOf(tab);
        if (tab.IsCurrent) ShowPage(Pages[index == 0 ? 1 : index - 1]);
        Pages.Remove(tab);
        _doc.Pages.Remove(tab.Page);
        if (_doc.LandingPage == tab.Name) _doc.LandingPage = null;
        UpdateLandingPage();
        OnPropertyChanged(nameof(CanDeletePage));
        OnPropertyChanged(nameof(PageIndicator));
        RaiseChanged();
    }

    string Snapshot() => JsonSerializer.Serialize(ToDocument());

    public void ResetHistory()
    {
        History.Reset(Snapshot());
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Records the edit for undo and tells the owner the document is unsaved.</summary>
    void RaiseChanged()
    {
        if (_restoring) return;
        History.Record(Snapshot());
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    void Undo() => Restore(History.Undo());

    bool CanUndo() => History.CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    void Redo() => Restore(History.Redo());

    bool CanRedo() => History.CanRedo;

    void Restore(string? snapshot)
    {
        if (snapshot is null) return;
        var selectedId = Selected?.Spec.Id;
        var pageIndex = CurrentPageIndex;
        _restoring = true;
        try
        {
            Apply(JsonSerializer.Deserialize<ReportDocument>(snapshot) ?? new ReportDocument(), pageIndex);
            Selected = Visuals.FirstOrDefault(v => v.Spec.Id == selectedId);
        }
        finally
        {
            _restoring = false;
        }
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public ReportDocument ToDocument()
    {
        _page.Visuals = Visuals.Select(v => v.Spec).ToList();
        return _doc;
    }

    /// <summary>Rebuilds the Data pane from the engine and re-runs every visual.</summary>
    public void RefreshTables()
    {
        var expanded = Tables.Where(t => t.IsExpanded).Select(t => t.Name).ToHashSet();
        var previewName = PreviewTable?.Name;
        Tables.Clear();
        foreach (var t in _engine.Tables)
            Tables.Add(new TableNodeViewModel(t, OnFieldToggled, _engine.Model, ShowHiddenFields) { IsExpanded = expanded.Contains(t.Name) });
        OnPropertyChanged(nameof(HasTables));
        ApplyFieldSearch();
        PreviewTable = Tables.FirstOrDefault(t => t.Name == previewName) ?? (IsDataView ? Tables.FirstOrDefault() : null);
        foreach (var v in Visuals) v.Refresh();
        SyncFieldChecks();
    }

    public void ExpandTables(IEnumerable<string> names)
    {
        var set = names.ToHashSet();
        foreach (var t in Tables) t.IsExpanded |= set.Contains(t.Name);
    }

    [RelayCommand]
    void ShowReportView() => IsDataView = IsModelView = false;

    [RelayCommand]
    void ShowModelView()
    {
        IsDataView = false;
        IsModelView = true;
    }

    [RelayCommand]
    void ShowDataView()
    {
        IsModelView = false;
        IsDataView = true;
    }

    [RelayCommand]
    void SelectPreviewTable(TableNodeViewModel table) => PreviewTable = table;

    /// <summary>Adds an empty visual of a type, as the ribbon's New visual does.</summary>
    [RelayCommand]
    void NewVisual(VisualType type)
    {
        ShowReportView();
        IsFormatTab = false;
        Selected = AddVisual(type, null);
    }

    /// <summary>Changes the selected visual's type, or adds a new visual if none is selected.</summary>
    [RelayCommand]
    void PickVisualType(VisualType type)
    {
        if (Selected is { ShowsData: true }) Selected.Type = type;
        else Selected = AddVisual(type, null);
    }

    [RelayCommand]
    void DeleteSelected()
    {
        if (Selected is null) return;
        Visuals.Remove(Selected);
        Selected = null;
        RaiseChanged();
    }

    [RelayCommand]
    void RemoveTable(TableNodeViewModel table)
    {
        _engine.DropTable(table.Name);
        RefreshTables();
        Changed?.Invoke(this, EventArgs.Empty);
        // Undo only covers the layout; restoring it can't bring the dropped data back.
        ResetHistory();
    }

    /// <summary>Adds a visual at the given point, or at the next free spot.</summary>
    public VisualViewModel AddVisual(VisualType type, Avalonia.Point? at)
    {
        var spec = new VisualSpec { Type = type };
        if (type == VisualType.Card) { spec.Width = 240; spec.Height = 140; }
        var (x, y) = at is { } p ? (p.X - spec.Width / 2, p.Y - 16) : NextFreeSpot(spec.Width, spec.Height);
        spec.X = Math.Clamp(Snap(x), 0, Math.Max(0, PageWidth - spec.Width));
        spec.Y = Math.Clamp(Snap(y), 0, Math.Max(0, PageHeight - spec.Height));
        var vm = CreateVisual(spec);
        Visuals.Add(vm);
        RaiseChanged();
        return vm;
    }

    /// <summary>
    /// Puts a field on a visual. With no visual, makes one (a table for text, a column chart
    /// for numbers). With no well, picks the natural one for the field and visual type.
    /// </summary>
    public void AddField(FieldInfo field, VisualViewModel? target = null, Well? well = null, Avalonia.Point? at = null)
    {
        target ??= AddVisual(field.IsNumeric ? VisualType.ColumnChart : VisualType.Table, at);
        target.AddField(field, well ?? NaturalWell(field, target.Type), DefaultDateGrain);
        Selected = target;
        SyncFieldChecks();
    }

    /// <summary>Lays out a small sales report over the sample table, for the Getting started card.</summary>
    public void BuildStarterReport(string table)
    {
        var fields = Tables.First(t => t.Name == table).Fields.ToDictionary(f => f.Name, f => f.Info);
        void Add(VisualType type, double x, double y, double w, double h, params string[] names)
        {
            var visual = AddVisual(type, null);
            foreach (var name in names) visual.AddField(fields[name], NaturalWell(fields[name], type), DefaultDateGrain);
            visual.SetGeometry(x, y, w, h);
        }
        Add(VisualType.Card, 16, 16, 240, 140, "Revenue");
        Add(VisualType.Card, 16, 172, 240, 140, "Units");
        Add(VisualType.ColumnChart, 272, 16, 480, 296, "Region", "Revenue");
        Add(VisualType.LineChart, 768, 16, 496, 296, "Date", "Revenue");
        Add(VisualType.BarChart, 16, 328, 560, 376, "Product", "Units");
        Add(VisualType.PieChart, 592, 328, 672, 376, "Channel", "Revenue");
        Selected = null;
        SyncFieldChecks();
    }

    static Well NaturalWell(FieldInfo field, VisualType type) =>
        type == VisualType.Card || field.IsNumeric || field.IsMeasure ? Well.Values : Well.Category;

    void OnFieldToggled(FieldNodeViewModel node, bool isChecked)
    {
        if (_syncingChecks) return;
        if (isChecked) AddField(node.Info, Selected);
        else
        {
            Selected?.RemoveField(node.Info);
            SyncFieldChecks();
        }
    }

    void OnVisualChanged(VisualViewModel visual)
    {
        if (visual == Selected) SyncFieldChecks();
        RaiseChanged();
    }

    void SyncFieldChecks()
    {
        _syncingChecks = true;
        try
        {
            foreach (var table in Tables)
                foreach (var f in table.AllFields)
                    f.IsChecked = Selected?.Uses(f.Info) ?? false;
        }
        finally
        {
            _syncingChecks = false;
        }
    }

    public double Snap(double v) => GridSize > 1 ? Math.Round(v / GridSize) * GridSize : Math.Round(v);

    /// <summary>Renames and resizes the page, keeping every visual on it.</summary>
    public void SetPage(string name, double width, double height)
    {
        name = string.IsNullOrWhiteSpace(name) ? _page.Name : name.Trim();
        if (name == _page.Name && width == _page.Width && height == _page.Height) return;
        // The landing page and bookmarks refer to pages by name.
        if (_doc.LandingPage == _page.Name) _doc.LandingPage = name;
        foreach (var b in _doc.Bookmarks ?? []) if (b.Page == _page.Name) b.Page = name;
        (_page.Name, _page.Width, _page.Height) = (name, width, height);
        Pages.FirstOrDefault(p => p.IsCurrent)?.Renamed();
        OnPropertyChanged(nameof(PageWidth));
        OnPropertyChanged(nameof(PageHeight));
        OnPropertyChanged(nameof(PageName));
        _restoring = true;
        try
        {
            foreach (var v in Visuals)
            {
                var w = Math.Min(v.Width, width);
                var h = Math.Min(v.Height, height);
                v.SetGeometry(Math.Clamp(v.X, 0, width - w), Math.Clamp(v.Y, 0, height - h), w, h);
            }
        }
        finally
        {
            _restoring = false;
        }
        RaiseChanged();
    }

    (double X, double Y) NextFreeSpot(double w, double h)
    {
        const double gap = 16;
        for (var y = gap; y + h <= PageHeight; y += 40)
            for (var x = gap; x + w <= PageWidth; x += 40)
                if (!Visuals.Any(v => x < v.X + v.Width + gap && v.X < x + w + gap && y < v.Y + v.Height + gap && v.Y < y + h + gap))
                    return (x, y);
        return (gap + Visuals.Count * 24 % 200, gap + Visuals.Count * 24 % 200);
    }
}
