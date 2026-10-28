using CommunityToolkit.Mvvm.ComponentModel;
using Tessera.Core;

namespace Tessera.App.ViewModels;

public sealed record FormatChoice(string Label, string? FormatString);

/// <summary>The New / Edit measure dialog: a name, a home table, a DAX formula and a number format.</summary>
public partial class MeasureViewModel : ObservableObject
{
    readonly DataEngine _engine;
    readonly Measure? _existing;

    public static IReadOnlyList<FormatChoice> Formats { get; } =
    [
        new("General", null), new("Whole number", "#,0"), new("Decimal number", "#,0.00"),
        new("Percentage", "0.0%"), new("Currency ($)", "\\$#,0;(\\$#,0);\\$#,0"),
    ];

    public MeasureViewModel(DataEngine engine, IReadOnlyList<string> tables, string? table, Measure? existing)
    {
        _engine = engine;
        _existing = existing;
        Tables = tables;
        Table = existing?.Table ?? (table is not null && tables.Contains(table) ? table : tables[0]);
        Name = existing?.Name ?? UniqueName();
        Expression = existing?.Expression ?? "";
        Format = Formats.FirstOrDefault(f => f.FormatString == existing?.FormatString)
            ?? (existing?.FormatString is { } custom ? new FormatChoice($"Custom ({custom})", custom) : Formats[0]);
    }

    public string Heading => _existing is null ? "New measure" : $"Edit measure [{_existing.Name}]";
    public IReadOnlyList<string> Tables { get; }

    [ObservableProperty]
    public partial string Table { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Expression { get; set; }

    [ObservableProperty]
    public partial FormatChoice Format { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>A preview of the measure over all rows, shown once it checks out.</summary>
    [ObservableProperty]
    public partial string? Result { get; set; }

    string UniqueName()
    {
        var taken = _engine.Model.Measures.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; ; i++)
            if (!taken.Contains($"Measure {i}")) return $"Measure {i}";
    }

    Measure Draft => new(Table, Name.Trim(), Expression.Trim(), Format.FormatString);

    public SemanticModel ApplyTo(SemanticModel model) =>
        model.WithMeasures([.. model.Measures.Where(m => m != _existing), Draft with
        {
            IsHidden = _existing?.IsHidden ?? false, DisplayFolder = _existing?.DisplayFolder,
        }]);

    /// <summary>Checks the name and formula by evaluating the measure; sets <see cref="Error"/> when it fails.</summary>
    public bool Validate()
    {
        Result = null;
        var name = Name.Trim();
        if (name.Length == 0) return Fail("Give the measure a name.");
        if (name.Contains('[') || name.Contains(']')) return Fail("Measure names can't contain [ or ].");
        if (_engine.Model.Measures.Any(m => m != _existing && string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)))
            return Fail($"There's already a measure called [{name}].");
        if (_engine.Tables.FirstOrDefault(t => t.Name == Table)?.Fields
                .Any(f => !f.IsMeasure && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) == true)
            return Fail($"{Table} already has a column called {name}.");
        if (Expression.Trim().Length == 0) return Fail("Write a DAX formula, for example SUM(Sales[Amount]).");

        var model = ApplyTo(_engine.Model);
        var draft = Draft;
        try
        {
            var visual = new VisualSpec
            {
                Type = VisualType.Card,
                Values = [new FieldRef(new FieldInfo(draft.Table, draft.Name, FieldKind.Number) { IsMeasure = true }, Aggregation.None)],
            };
            // Evaluate against a throwaway copy of the model so a bad formula never reaches the report.
            var value = _engine.Query(visual, 1, model: model).Rows[0][0];
            Result = value is null ? "(Blank)" : Sql.ToDouble(value) is { } d && value is not string
                ? Sql.FormatNumber(d, draft.FormatString, compact: false) : Sql.Format(value);
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or FormatException
            or DuckDB.NET.Data.DuckDBException)
        {
            return Fail(e.Message);
        }
        Error = null;
        return true;
    }

    bool Fail(string message)
    {
        Error = message;
        return false;
    }
}
