using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Tessera.Core;

namespace Tessera.App.ViewModels;

public partial class VisualViewModel : ObservableObject
{
    readonly DataEngine _engine;
    readonly Action<VisualViewModel> _changed;
    readonly Func<IReadOnlyList<FieldFilter>>? _pageFilters;

    /// <param name="pageFilters">The filters of the page the visual is on.</param>
    public VisualViewModel(VisualSpec spec, DataEngine engine, Action<VisualViewModel> changed,
        Func<IReadOnlyList<FieldFilter>>? pageFilters = null)
    {
        Spec = spec;
        _engine = engine;
        _changed = changed;
        _pageFilters = pageFilters;
        RebuildWells();
        Refresh();
    }

    public VisualSpec Spec { get; }

    public ObservableCollection<WellFieldViewModel> CategoryFields { get; } = [];
    public ObservableCollection<WellFieldViewModel> ValueFields { get; } = [];

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial QueryResult? Result { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>A <see cref="Decomposition"/> or <see cref="KeyInfluencerAnalysis"/> for Power BI's AI visuals.</summary>
    [ObservableProperty]
    public partial object? Analysis { get; private set; }

    public VisualType Type
    {
        get => Spec.Type;
        set
        {
            if (Spec.Type == value) return;
            Spec.Type = value;
            Normalize();
            RebuildWells();
            OnPropertyChanged();
            OnPropertyChanged(nameof(CategoryWellLabel));
            OnPropertyChanged(nameof(HasCategoryWell));
            OnPropertyChanged(nameof(ValuesWellLabel));
            OnPropertyChanged(nameof(ShowsData));
            OnPropertyChanged(nameof(IsText));
            OnPropertyChanged(nameof(Subtitle));
            Refresh();
            _changed(this);
        }
    }

    // Geometry is the desktop layout's, or the mobile layout's while the report shows that.
    bool OnMobile => UseMobile && Spec.Mobile is not null;
    public double X { get => OnMobile ? Spec.Mobile!.X : Spec.X; set => SetGeometry(value, Y, Width, Height); }
    public double Y { get => OnMobile ? Spec.Mobile!.Y : Spec.Y; set => SetGeometry(X, value, Width, Height); }
    public double Width => OnMobile ? Spec.Mobile!.Width : Spec.Width;
    public double Height => OnMobile ? Spec.Mobile!.Height : Spec.Height;

    public void SetGeometry(double x, double y, double width, double height)
    {
        if (x == X && y == Y && width == Width && height == Height) return;
        if (OnMobile) Spec.Mobile = new MobilePosition(x, y, width, height);
        else (Spec.X, Spec.Y, Spec.Width, Spec.Height) = (x, y, width, height);
        NotifyGeometry();
        _changed(this);
    }

    void NotifyGeometry()
    {
        OnPropertyChanged(nameof(X));
        OnPropertyChanged(nameof(Y));
        OnPropertyChanged(nameof(Width));
        OnPropertyChanged(nameof(Height));
        OnPropertyChanged(nameof(IsOnCanvas));
    }

    /// <summary>Set by the report while View › Mobile layout is on.</summary>
    public bool UseMobile
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            NotifyGeometry();
        }
    }

    /// <summary>Puts the visual on the mobile layout at the given place.</summary>
    public void PlaceOnMobile(MobilePosition position)
    {
        Spec.Mobile = position;
        NotifyGeometry();
        _changed(this);
    }

    /// <summary>Format painter: takes another visual's look, keeping its own text and picture.</summary>
    public void ApplyFormat(VisualStyle source)
    {
        var style = System.Text.Json.JsonSerializer.Deserialize<VisualStyle>(System.Text.Json.JsonSerializer.Serialize(source))!;
        (style.Text, style.Image) = (Spec.Style?.Text, Spec.Style?.Image);
        Spec.Style = style;
        OnPropertyChanged(nameof(Style));
        OnPropertyChanged(string.Empty);
        _changed(this);
    }

    /// <summary>Selection pane's eye: hidden visuals stay in the report but aren't drawn.</summary>
    public bool IsHidden
    {
        get => Spec.Hidden;
        set
        {
            if (Spec.Hidden == value) return;
            Spec.Hidden = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsOnCanvas));
            _changed(this);
        }
    }

    /// <summary>Drawn on the page: not hidden, and on the mobile layout when that's showing.</summary>
    public bool IsOnCanvas => !Spec.Hidden && (!UseMobile || Spec.Mobile is not null);

    /// <summary>The name the Selection pane and Performance analyzer show.</summary>
    public string LayerName => Spec.Title ?? (ShowsData && !IsEmpty ? AutoTitle : Type switch
    {
        VisualType.Text => "Text box",
        VisualType.Image => "Image",
        VisualType.Shape when Spec.Style?.Shape is { } shape => shape == "RoundedRectangle" ? "Rounded rectangle" : shape,
        VisualType.Shape => "Rectangle",
        VisualType.PageNavigator => "Page navigator",
        VisualType.BookmarkNavigator => "Bookmark navigator",
        VisualType.Unsupported => Spec.Style?.Text ?? "Visual",
        _ => ReportViewModel.VisualTypes.FirstOrDefault(t => t.Type == Type)?.Label ?? Type.ToString(),
    });

    /// <summary>Raised after each data query with how long the engine took, for the Performance analyzer.</summary>
    public event Action<VisualViewModel, TimeSpan>? Queried;

    /// <summary>User-entered title; empty means use the generated one.</summary>
    public string? CustomTitle
    {
        get => Spec.Title;
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? null : value;
            if (v == Spec.Title) return;
            Spec.Title = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Subtitle));
            _changed(this);
        }
    }

    public string Title => Spec.Title ?? AutoTitle;

    /// <summary>"by Region" under a generated title, as Power BI shows it.</summary>
    public string? Subtitle => Spec.Title is null && ShowsData && Type is not (VisualType.Table or VisualType.Card) && !Type.IsAnalysis()
        && Spec.Values.Count > 0 && Spec.Category.Count > 0
            ? "by " + string.Join(", ", Spec.Category.Select(c => c.DisplayName))
            : null;

    /// <summary>False for text boxes, shapes, images and other layout elements.</summary>
    public bool ShowsData => Type.ShowsData();

    public VisualStyle? Style => Spec.Style;

    string AutoTitle
    {
        get
        {
            if (!ShowsData) return Type == VisualType.Unsupported ? Spec.Style?.Text ?? "" : "";
            var values = string.Join(" and ", Spec.Values.Select(v => v.DisplayName));
            var cats = string.Join(", ", Spec.Category.Select(c => c.DisplayName));
            if (Type is VisualType.Table or VisualType.Card) return "";
            if (Type == VisualType.DecompositionTree) return "Decomposition tree";
            if (Type == VisualType.KeyInfluencers) return "Key influencers";
            return values.Length == 0 ? cats : values;
        }
    }

    public bool HasCategoryWell => Type != VisualType.Card && ShowsData;

    // ---- Format pane ----

    VisualStyle EditStyle() => Spec.Style ??= new VisualStyle();

    void StyleChanged(string property)
    {
        OnPropertyChanged(property);
        OnPropertyChanged(nameof(Style));
        _changed(this);
    }

    public bool ShowTitle
    {
        get => Spec.Style?.HideTitle != true;
        set
        {
            if (value == ShowTitle) return;
            EditStyle().HideTitle = !value;
            StyleChanged(nameof(ShowTitle));
        }
    }

    /// <summary>#RRGGBB, or null for the default.</summary>
    public string? BackgroundColor
    {
        get => Spec.Style?.Background;
        set
        {
            if (value == BackgroundColor) return;
            EditStyle().Background = value;
            StyleChanged(nameof(BackgroundColor));
        }
    }

    public string? AccentColor
    {
        get => Spec.Style?.Accent;
        set
        {
            if (value == AccentColor) return;
            EditStyle().Accent = value;
            StyleChanged(nameof(AccentColor));
        }
    }

    /// <summary>Colour swatches in the Format pane; the first entry (null) restores the default.</summary>
    public static IReadOnlyList<string?> Swatches { get; } =
    [
        null, "#FFFFFF", "#F3F2F1", "#D6E4F0", "#1A2E4B", "#264570", "#335C96", "#0072BF", "#118DFF",
        "#0E7C66", "#30B041", "#E0A526", "#E66C37", "#D64550", "#744EC2", "#252423",
    ];

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    void SetBackground(string? color) => BackgroundColor = color;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    void SetAccent(string? color) => AccentColor = color;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    void SetTextColor(string? color) => TextColor = color;

    public bool IsText => Type == VisualType.Text;

    public string? Text
    {
        get => Spec.Style?.Text;
        set
        {
            if (value == Text) return;
            EditStyle().Text = value;
            StyleChanged(nameof(Text));
        }
    }

    public decimal? FontSize
    {
        get => (decimal)(Spec.Style?.FontSize ?? 14);
        set
        {
            var v = (double)(value ?? 14);
            if (v == Spec.Style?.FontSize) return;
            EditStyle().FontSize = v;
            StyleChanged(nameof(FontSize));
        }
    }

    public bool TextBold
    {
        get => Spec.Style?.Bold == true;
        set
        {
            if (value == TextBold) return;
            EditStyle().Bold = value;
            StyleChanged(nameof(TextBold));
        }
    }

    public string? TextColor
    {
        get => Spec.Style?.Foreground;
        set
        {
            if (value == TextColor) return;
            EditStyle().Foreground = value;
            StyleChanged(nameof(TextColor));
        }
    }

    public string CategoryWellLabel => Type switch
    {
        VisualType.DecompositionTree or VisualType.KeyInfluencers => "Explain by",
        VisualType.Table => "Columns",
        VisualType.BarChart => "Y-axis",
        VisualType.PieChart or VisualType.DonutChart => "Legend",
        _ => "X-axis",
    };

    public string ValuesWellLabel => Type switch
    {
        VisualType.DecompositionTree or VisualType.KeyInfluencers => "Analyze",
        VisualType.Card => "Field",
        VisualType.BarChart => "X-axis",
        VisualType.ColumnChart or VisualType.LineChart or VisualType.AreaChart => "Y-axis",
        _ => "Values",
    };

    public bool IsEmpty => Spec.Category.Count == 0 && Spec.Values.Count == 0;

    public bool Uses(FieldInfo f) => Spec.Category.Concat(Spec.Values).Any(r => r.Refers(f));

    public void AddField(FieldInfo field, Well well, DateGrain axisGrain = DateGrain.Month)
    {
        if (!ShowsData) return;
        if (field.IsMeasure) well = Well.Values;   // a measure is always a summarized value
        var list = well == Well.Category ? Spec.Category : Spec.Values;
        if (list.Any(r => r.Refers(field))) return;

        if (Type == VisualType.KeyInfluencers && field.IsMeasure) return;   // it explains a column, row by row
        var agg = well == Well.Category ? Aggregation.None
            : Type == VisualType.Table && !field.IsNumeric ? Aggregation.None
            : Type == VisualType.KeyInfluencers ? Aggregation.None
            : Aggregations.DefaultFor(field.Kind);
        var single = Type is VisualType.Card or VisualType.PieChart or VisualType.DonutChart || (well == Well.Values && Type.IsAnalysis())
            || (well == Well.Category && Type != VisualType.Table && !Type.IsAnalysis());
        if (single) list.Clear();
        var grain = well == Well.Category && field.Kind == FieldKind.Date && Type != VisualType.Table
            ? axisGrain : DateGrain.None;
        list.Add(new FieldRef(field, agg) { Grain = grain });
        FieldsChanged();
    }

    public void RemoveField(FieldInfo field)
    {
        Spec.Category.RemoveAll(r => r.Refers(field));
        Spec.Values.RemoveAll(r => r.Refers(field));
        FieldsChanged();
    }

    internal void RemoveRef(FieldRef r)
    {
        Spec.Category.Remove(r);
        Spec.Values.Remove(r);
        FieldsChanged();
    }

    internal void FieldOptionChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        Refresh();
        _changed(this);
    }

    void FieldsChanged()
    {
        // A different set of levels makes the expanded path meaningless.
        Spec.Drill = null;
        RebuildWells();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        Refresh();
        _changed(this);
    }

    /// <summary>Drops fields the new type can't show and fixes aggregations.</summary>
    void Normalize()
    {
        if (Type == VisualType.Card)
        {
            Spec.Values = Spec.Values.Concat(Spec.Category).Take(1).ToList();
            Spec.Category.Clear();
        }
        if (Type != VisualType.Table && !Type.IsAnalysis() && Spec.Category.Count > 1) Spec.Category = Spec.Category.Take(1).ToList();
        if (Type is VisualType.PieChart or VisualType.DonutChart or VisualType.DecompositionTree or VisualType.KeyInfluencers && Spec.Values.Count > 1)
            Spec.Values = Spec.Values.Take(1).ToList();
        foreach (var c in Spec.Category.Where(c => c.IsSummarized && Type.IsAnalysis())) c.Aggregation = Aggregation.None;
        if (Type == VisualType.KeyInfluencers)
        {
            Spec.Values.RemoveAll(v => v.IsMeasure);
            foreach (var v in Spec.Values) v.Aggregation = Aggregation.None;
        }
        else if (Type != VisualType.Table)
            foreach (var v in Spec.Values.Where(v => !v.IsSummarized))
                v.Aggregation = Aggregations.DefaultFor(v.Kind);
        if (Type != VisualType.DecompositionTree) Spec.Drill = null;
    }

    void RebuildWells()
    {
        CategoryFields.Clear();
        foreach (var r in Spec.Category) CategoryFields.Add(new WellFieldViewModel(this, r, canSummarize: false));
        ValueFields.Clear();
        foreach (var r in Spec.Values) ValueFields.Add(new WellFieldViewModel(this, r, canSummarize: Type != VisualType.KeyInfluencers));
    }

    /// <summary>Opens a decomposition tree node, or closes it when it's already open.</summary>
    public void Expand(int level, string? key)
    {
        var drill = (Spec.Drill ?? []).Take(level).ToList();
        if (Spec.Drill is { } current && current.Count > level && current[level] == key)
            Spec.Drill = drill.Count > 0 ? drill : null;
        else
            Spec.Drill = [.. drill, key];
        Refresh();
        _changed(this);
    }

    /// <summary>The key influencers' target value, e.g. "Lost" to see what influences losing.</summary>
    public string? TargetValue
    {
        get => Spec.TargetValue;
        set
        {
            if (value == Spec.TargetValue || value is null) return;
            Spec.TargetValue = value;
            OnPropertyChanged();
            Refresh();
            _changed(this);
        }
    }

    /// <summary>Optimize › Pause visuals: while this says so, queries wait and the visual is marked stale.</summary>
    public Func<bool>? IsPaused { get; set; }

    public void Refresh()
    {
        if (IsPaused?.Invoke() == true) return;
        Analysis = null;
        if (IsEmpty || !ShowsData)
        {
            Result = null;
            Error = null;
            return;
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        if (Type.IsAnalysis())
        {
            Result = null;
            try
            {
                object analysis = Type == VisualType.DecompositionTree
                    ? _engine.Decompose(Spec, _pageFilters?.Invoke())
                    : _engine.KeyInfluencers(Spec, _pageFilters?.Invoke());
                // Report the query before the visual redraws, so its display time lands on this row.
                Queried?.Invoke(this, clock.Elapsed);
                Analysis = analysis;
                Error = null;
            }
            catch (Exception e)
            {
                Error = e.Message;
            }
            return;
        }
        var limit = Type switch
        {
            VisualType.Table => 1000,
            VisualType.LineChart or VisualType.AreaChart => 2000,
            VisualType.PieChart or VisualType.DonutChart => 12,
            _ => 50,
        };
        try
        {
            var result = _engine.Query(Spec, limit, _pageFilters?.Invoke());
            Queried?.Invoke(this, clock.Elapsed);
            Result = result;
            Error = null;
        }
        catch (Exception e)
        {
            Result = null;
            Error = e.Message;
        }
    }
}
