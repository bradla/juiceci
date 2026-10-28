using System.Text.Json.Serialization;

namespace Tessera.Core;

public enum FieldKind { Text, Number, Date, Boolean }

public sealed record FieldInfo(string Table, string Name, FieldKind Kind)
{
    public bool IsNumeric => Kind == FieldKind.Number;

    /// <summary>A model measure (a DAX formula) rather than a stored column.</summary>
    public bool IsMeasure { get; init; }

    /// <summary>Power BI format string, e.g. <c>0.0%</c> or <c>\$#,0</c>.</summary>
    public string? FormatString { get; init; }

    /// <summary>Hidden in Power BI's Data pane (keys, helper columns); still usable in measures and joins.</summary>
    public bool IsHidden { get; init; }

    /// <summary>Data pane folder, e.g. "Address"; nested folders are separated by backslashes.</summary>
    public string? DisplayFolder { get; init; }
}

public sealed record TableInfo(string Name, IReadOnlyList<FieldInfo> Fields, long RowCount);

[JsonConverter(typeof(JsonStringEnumConverter<Aggregation>))]
public enum Aggregation { None, Sum, Average, Min, Max, Count, CountDistinct }

/// <summary>How a date field on an axis is bucketed.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DateGrain>))]
public enum DateGrain { None, Year, Quarter, Month, Day }

[JsonConverter(typeof(JsonStringEnumConverter<VisualType>))]
public enum VisualType
{
    Table, Card, BarChart, ColumnChart, LineChart, PieChart,

    // Power BI's AI visuals: their own analysis rather than one grouped query.
    DecompositionTree, KeyInfluencers,

    // Layout elements brought in from Power BI reports; they show no data.
    Text, Shape, Image, PageNavigator,

    /// <summary>A Power BI visual JuiceCI can't draw; kept so the page layout stays intact.</summary>
    Unsupported,
}

public static class VisualTypes
{
    public static bool ShowsData(this VisualType t) => t <= VisualType.KeyInfluencers;

    /// <summary>Visuals drawn from <see cref="DataEngine.Decompose"/> or <see cref="DataEngine.KeyInfluencers"/>.</summary>
    public static bool IsAnalysis(this VisualType t) => t is VisualType.DecompositionTree or VisualType.KeyInfluencers;
}

public static class Aggregations
{
    public static Aggregation DefaultFor(FieldKind kind) =>
        kind == FieldKind.Number ? Aggregation.Sum : Aggregation.Count;

    /// <summary>Aggregations that make sense for a field of the given kind.</summary>
    public static IReadOnlyList<Aggregation> AllowedFor(FieldKind kind) => kind switch
    {
        FieldKind.Number => [Aggregation.None, Aggregation.Sum, Aggregation.Average, Aggregation.Min,
            Aggregation.Max, Aggregation.Count, Aggregation.CountDistinct],
        FieldKind.Date => [Aggregation.None, Aggregation.Min, Aggregation.Max, Aggregation.Count,
            Aggregation.CountDistinct],
        _ => [Aggregation.None, Aggregation.Count, Aggregation.CountDistinct],
    };

    public static string Label(Aggregation a) => a switch
    {
        Aggregation.None => "Don't summarize",
        Aggregation.Sum => "Sum",
        Aggregation.Average => "Average",
        Aggregation.Min => "Minimum",
        Aggregation.Max => "Maximum",
        Aggregation.Count => "Count",
        Aggregation.CountDistinct => "Count (distinct)",
        _ => a.ToString(),
    };
}

public sealed class FieldRef
{
    public string Table { get; set; } = "";
    public string Column { get; set; } = "";
    public FieldKind Kind { get; set; }
    public Aggregation Aggregation { get; set; }
    public DateGrain Grain { get; set; }

    public FieldRef() { }

    public FieldRef(FieldInfo field, Aggregation aggregation)
    {
        Table = field.Table;
        Column = field.Name;
        Kind = field.Kind;
        Aggregation = field.IsMeasure ? Aggregation.None : aggregation;
        IsMeasure = field.IsMeasure;
        FormatString = field.FormatString;
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsMeasure { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FormatString { get; set; }

    /// <summary>True for values computed per group: measures and summarized columns.</summary>
    [JsonIgnore]
    public bool IsSummarized => IsMeasure || Aggregation != Aggregation.None;

    public bool Refers(FieldInfo f) => Table == f.Table && Column == f.Name && IsMeasure == f.IsMeasure;

    /// <summary>A name the report gave this field (Power BI's "Rename for this visual").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; set; }

    [JsonIgnore]
    public string DisplayName => Label ?? (IsMeasure ? Column
        : Aggregation != Aggregation.None ? $"{Aggregations.Label(Aggregation)} of {Column}"
        : Grain != DateGrain.None ? $"{Column} ({Grain})"
        : Column);
}

public sealed class VisualSpec
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public VisualType Type { get; set; }
    public string? Title { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 420;
    public double Height { get; set; } = 280;

    /// <summary>Axis / rows: the fields results are grouped by.</summary>
    public List<FieldRef> Category { get; set; } = [];

    /// <summary>The fields that are summarized (or shown as-is for "Don't summarize").</summary>
    public List<FieldRef> Values { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VisualStyle? Style { get; set; }

    /// <summary>Filters that apply to this visual only.</summary>
    public List<FieldFilter> Filters { get; set; } = [];

    /// <summary>Decomposition tree: the value expanded at each level, in <see cref="Category"/> order (null is blank).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string?>? Drill { get; set; }

    /// <summary>Key influencers: the value of the target field being explained, e.g. "Won".</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetValue { get; set; }
}

/// <summary>
/// Keeps rows whose field, as text, is one of <see cref="Values"/> (null stands for blank), and that
/// pass its <see cref="Rules"/>. With neither it doesn't filter, like Power BI's "(All)".
/// </summary>
public sealed class FieldFilter
{
    public FieldRef Field { get; set; } = new();
    public List<string?> Values { get; set; } = [];

    /// <summary>Advanced filtering: conditions such as "is not blank" or "contains".</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<FilterRule>? Rules { get; set; }

    /// <summary>Rows must pass every rule, or any rule when false.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool MatchAny { get; set; }

    [JsonIgnore]
    public bool IsActive => Values.Count > 0 || Rules is { Count: > 0 };
}

[JsonConverter(typeof(JsonStringEnumConverter<FilterOperator>))]
public enum FilterOperator
{
    Is, IsNot, LessThan, LessOrEqual, GreaterThan, GreaterOrEqual,
    Contains, DoesNotContain, StartsWith, DoesNotStartWith, IsBlank, IsNotBlank,
}

/// <summary>One condition of an advanced filter; <see cref="Value"/> is unused for the blank tests.</summary>
public sealed record FilterRule(FilterOperator Operator, string? Value = null)
{
    public string Describe() => Operator switch
    {
        FilterOperator.Is => $"is {Value}",
        FilterOperator.IsNot => $"is not {Value}",
        FilterOperator.LessThan => $"is less than {Value}",
        FilterOperator.LessOrEqual => $"is less than or equal to {Value}",
        FilterOperator.GreaterThan => $"is greater than {Value}",
        FilterOperator.GreaterOrEqual => $"is greater than or equal to {Value}",
        FilterOperator.Contains => $"contains {Value}",
        FilterOperator.DoesNotContain => $"does not contain {Value}",
        FilterOperator.StartsWith => $"starts with {Value}",
        FilterOperator.DoesNotStartWith => $"does not start with {Value}",
        FilterOperator.IsBlank => "is blank",
        _ => "is not blank",
    };
}

/// <summary>Formatting carried over from an imported report; null properties use JuiceCI's defaults.</summary>
public sealed class VisualStyle
{
    /// <summary>Text box content, or the name of an unsupported visual.</summary>
    public string? Text { get; set; }
    public double? FontSize { get; set; }
    public bool Bold { get; set; }
    /// <summary>Colours as #RRGGBB.</summary>
    public string? Foreground { get; set; }
    public string? Background { get; set; }
    /// <summary>Colour of the first data series.</summary>
    public string? Accent { get; set; }
    public string? TitleColor { get; set; }
    /// <summary>Hide the value axis and its gridlines, leaving data labels (Power BI's "Data labels" style).</summary>
    public bool HideValueAxis { get; set; }
    public bool HideTitle { get; set; }
    /// <summary>Base64-encoded PNG or JPEG for image visuals.</summary>
    public string? Image { get; set; }
}

public sealed class ReportPage
{
    public string Name { get; set; } = "Page 1";
    public double Width { get; set; } = 1280;
    public double Height { get; set; } = 720;
    public List<VisualSpec> Visuals { get; set; } = [];

    /// <summary>#RRGGBB behind the visuals; null is white.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Background { get; set; }

    /// <summary>Filters that apply to every visual on the page.</summary>
    public List<FieldFilter> Filters { get; set; } = [];
}

public sealed class ReportDocument
{
    public List<ReportPage> Pages { get; set; } = [new ReportPage()];
}

/// <summary>A many-to-one link: each row of <see cref="FromTable"/> matches at most one row of <see cref="ToTable"/>.</summary>
public sealed record Relationship(string FromTable, string FromColumn, string ToTable, string ToColumn, bool IsActive = true);

/// <summary>A named DAX formula evaluated per group, like Power BI's measures.</summary>
public sealed record Measure(string Table, string Name, string Expression, string? FormatString = null,
    bool IsHidden = false, string? DisplayFolder = null);

/// <summary>How a column shows in the Data pane.</summary>
public sealed record ColumnDisplay(string Table, string Column, bool IsHidden, string? DisplayFolder, string? FormatString = null);

/// <summary>A drill path of columns, e.g. Country → State → City.</summary>
public sealed record Hierarchy(string Table, string Name, List<string> Levels, string? DisplayFolder = null);

/// <summary>Values of <see cref="Column"/> are listed in the order of <see cref="ByColumn"/>, e.g. month names by month number.</summary>
public sealed record SortRule(string Table, string Column, string ByColumn);

/// <summary>Relationships, measures and sort orders over the engine's tables, as imported from a Power BI model.</summary>
public sealed class SemanticModel
{
    public List<Relationship> Relationships { get; set; } = [];
    public List<Measure> Measures { get; set; } = [];
    public List<SortRule> SortRules { get; set; } = [];
    public List<ColumnDisplay> Columns { get; set; } = [];
    public List<Hierarchy> Hierarchies { get; set; } = [];
    public List<string> HiddenTables { get; set; } = [];

    public ColumnDisplay? FindColumn(string table, string column) =>
        Columns.FirstOrDefault(c => Same(c.Table, table) && Same(c.Column, column));

    public bool IsEmpty => Relationships.Count + Measures.Count + SortRules.Count + Columns.Count + Hierarchies.Count
        + HiddenTables.Count == 0;

    /// <summary>A copy without anything that refers to a table, after it's removed.</summary>
    public SemanticModel Without(string table)
    {
        bool Keep(string t) => !Same(t, table);
        return new SemanticModel
        {
            Relationships = Relationships.Where(r => Keep(r.FromTable) && Keep(r.ToTable)).ToList(),
            Measures = Measures.Where(m => Keep(m.Table)).ToList(),
            SortRules = SortRules.Where(r => Keep(r.Table)).ToList(),
            Columns = Columns.Where(c => Keep(c.Table)).ToList(),
            Hierarchies = Hierarchies.Where(h => Keep(h.Table)).ToList(),
            HiddenTables = HiddenTables.Where(Keep).ToList(),
        };
    }

    /// <summary>This model plus another's contents, with the other's table names mapped (after renames on import).</summary>
    public SemanticModel Merge(SemanticModel other, Func<string, string> map) => new()
    {
        Relationships = [.. Relationships, .. other.Relationships.Select(r => r with { FromTable = map(r.FromTable), ToTable = map(r.ToTable) })],
        Measures = [.. Measures, .. other.Measures.Select(m => m with { Table = map(m.Table) })],
        SortRules = [.. SortRules, .. other.SortRules.Select(r => r with { Table = map(r.Table) })],
        Columns = [.. Columns, .. other.Columns.Select(c => c with { Table = map(c.Table) })],
        Hierarchies = [.. Hierarchies, .. other.Hierarchies.Select(h => h with { Table = map(h.Table) })],
        HiddenTables = [.. HiddenTables, .. other.HiddenTables.Select(map)],
    };

    /// <summary>A copy with a different measure list.</summary>
    public SemanticModel WithMeasures(List<Measure> measures) => new()
    {
        Relationships = Relationships, Measures = measures, SortRules = SortRules,
        Columns = Columns, Hierarchies = Hierarchies, HiddenTables = HiddenTables,
    };

    public SortRule? FindSortRule(string table, string column) =>
        SortRules.FirstOrDefault(r => Same(r.Table, table) && Same(r.Column, column));

    public Measure? FindMeasure(string table, string name) =>
        Measures.FirstOrDefault(m => Same(m.Table, table) && Same(m.Name, name));

    /// <summary>Measure names are unique across a Power BI model, so <c>[Name]</c> needs no table.</summary>
    public Measure? FindMeasure(string name) => Measures.FirstOrDefault(m => Same(m.Name, name));

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

public sealed record QueryResult
{
    public required IReadOnlyList<string> Columns { get; init; }
    public required IReadOnlyList<object?[]> Rows { get; init; }

    /// <summary>Number of leading columns that are categories (the rest are values).</summary>
    public required int CategoryCount { get; init; }

    /// <summary>Power BI format string per column, where one is known.</summary>
    public IReadOnlyList<string?> Formats { get; init; } = [];

    public string? FormatOf(int column) => column < Formats.Count ? Formats[column] : null;
}
