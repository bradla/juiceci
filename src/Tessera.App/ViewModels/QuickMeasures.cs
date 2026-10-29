using Tessera.Core;

namespace Tessera.App.ViewModels;

public sealed record QuickMeasure(string Key, string Label);

/// <summary>A Quick measure, written out as DAX for the measure editor to review.</summary>
public sealed record QuickMeasureDraft(string Table, string Name, string Expression, string? FormatString);

/// <summary>
/// Home › Quick measure: Power BI's common calculations, filled in with a sensible column so the
/// measure editor opens with working DAX to adjust. Only calculations JuiceCI's DAX can evaluate are offered.
/// </summary>
public static class QuickMeasures
{
    public static IReadOnlyList<QuickMeasure> All { get; } =
    [
        new("average", "Average"),
        new("distinct", "Count of distinct values"),
        new("filtered", "Filtered value"),
        new("difference", "Difference from filtered value"),
        new("percentDifference", "Percentage difference from filtered value"),
        new("ratio", "Ratio of two values"),
        new("weighted", "Weighted average"),
    ];

    /// <param name="preferred">A value field to build on, such as the selected visual's; null picks one.</param>
    public static QuickMeasureDraft? Build(string key, DataEngine engine, FieldRef? preferred)
    {
        var tables = engine.Tables.Where(t => t.Fields.Any(f => !f.IsMeasure && f.IsNumeric)).ToList();
        var table = preferred is { IsMeasure: false } p && tables.FirstOrDefault(t => t.Name == p.Table) is { } own ? own : tables.FirstOrDefault();
        if (table is null) return null;
        var numbers = table.Fields.Where(f => !f.IsMeasure && f.IsNumeric).ToList();
        var value = numbers.FirstOrDefault(f => f.Name == preferred?.Column) ?? numbers[0];
        var weight = numbers.FirstOrDefault(f => f != value) ?? value;
        var category = table.Fields.FirstOrDefault(f => !f.IsMeasure && f.Kind == FieldKind.Text);

        string Col(FieldInfo f) => $"{Ref(table.Name)}[{f.Name}]";
        string? filter = null, item = null;
        if (category is not null && engine.DistinctValues(table.Name, category.Name).FirstOrDefault(v => v is not null) is { } first)
        {
            item = first;
            filter = $"CALCULATE(SUM({Col(value)}), {Col(category)} = \"{first.Replace("\"", "\"\"")}\")";
        }

        QuickMeasureDraft Draft(string name, string expression, string? format = null) => new(table.Name, name, expression, format);
        return key switch
        {
            "average" => Draft($"Average of {value.Name}", $"AVERAGE({Col(value)})"),
            "distinct" when category is not null => Draft($"Count of {category.Name}", $"DISTINCTCOUNT({Col(category)})", "#,0"),
            "filtered" when filter is not null => Draft($"{value.Name} for {item}", filter),
            "difference" when filter is not null =>
                Draft($"{value.Name} difference from {item}", $"SUM({Col(value)}) - {filter}"),
            "percentDifference" when filter is not null =>
                Draft($"{value.Name} % difference from {item}", $"DIVIDE(SUM({Col(value)}) - {filter}, {filter})", "0.0%"),
            "ratio" => Draft($"{value.Name} per {weight.Name}", $"DIVIDE(SUM({Col(value)}), SUM({Col(weight)}))"),
            "weighted" => Draft($"{value.Name} weighted by {weight.Name}",
                $"DIVIDE(SUMX({Ref(table.Name)}, {Col(value)} * {Col(weight)}), SUM({Col(weight)}))"),
            _ => null,
        };
    }

    static string Ref(string table) => "'" + table.Replace("'", "''") + "'";
}
