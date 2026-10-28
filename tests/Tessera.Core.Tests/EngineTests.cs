using Tessera.Core;

namespace Tessera.Core.Tests;

public sealed class EngineTests : IDisposable
{
    readonly DataEngine _engine = new();
    readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("juiceci-test-");

    public void Dispose()
    {
        _engine.Dispose();
        _dir.Delete(recursive: true);
    }

    string WriteCsv(string name, string content)
    {
        var path = Path.Combine(_dir.FullName, name);
        File.WriteAllText(path, content);
        return path;
    }

    static FieldRef Field(TableInfo t, string column, Aggregation agg = Aggregation.None) =>
        new(t.Fields.Single(f => f.Name == column), agg);

    [Fact]
    public void ImportsCsvWithInferredTypes()
    {
        var names = _engine.ImportFile(WriteCsv("orders.csv", "Region,Amount,When\nNorth,10.5,2024-01-02\nSouth,4,2024-02-03\n"));

        var table = Assert.Single(_engine.Tables);
        Assert.Equal(["orders"], names);
        Assert.Equal(2, table.RowCount);
        Assert.Equal([FieldKind.Text, FieldKind.Number, FieldKind.Date], table.Fields.Select(f => f.Kind));
    }

    [Fact]
    public void ImportsCsvAsTextWhenTypeDetectionIsOff()
    {
        _engine.Import = new ImportOptions(DetectTypes: false);
        _engine.ImportFile(WriteCsv("orders.csv", "Region,Amount\nNorth,10.5\n"));

        Assert.All(Assert.Single(_engine.Tables).Fields, f => Assert.Equal(FieldKind.Text, f.Kind));
    }

    [Fact]
    public void EngineResourceLimitsCanBeSetAndReset()
    {
        _engine.SetResourceLimits(2, 512);
        _engine.SetResourceLimits(null, null);
    }

    [Fact]
    public void ImportingSameNameTwiceGetsUniqueName()
    {
        var path = WriteCsv("t.csv", "a\n1\n");
        _engine.ImportFile(path);
        Assert.Equal(["t (2)"], _engine.ImportFile(path));
    }

    [Fact]
    public void ColumnChartSumsByCategoryLargestFirst()
    {
        _engine.ImportFile(WriteCsv("s.csv", "Region,Amount\nNorth,1\nSouth,5\nNorth,2\nEast,\n"));
        var t = _engine.Tables[0];
        var visual = new VisualSpec
        {
            Type = VisualType.ColumnChart,
            Category = [Field(t, "Region")],
            Values = [Field(t, "Amount", Aggregation.Sum)],
        };

        var result = _engine.Query(visual);

        Assert.Equal(["Region", "Sum of Amount"], result.Columns);
        Assert.Equal(1, result.CategoryCount);
        Assert.Equal(["South", "North", "East"], result.Rows.Select(r => (string)r[0]!));
        Assert.Equal([5.0, 3.0, null], result.Rows.Select(r => (double?)r[1]));
    }

    [Fact]
    public void TableWithDontSummarizeGroupsByThatColumn()
    {
        _engine.ImportFile(WriteCsv("s.csv", "Region,Amount\nNorth,1\nNorth,1\nSouth,2\n"));
        var t = _engine.Tables[0];
        var visual = new VisualSpec { Type = VisualType.Table, Values = [Field(t, "Amount")], Category = [Field(t, "Region")] };

        var result = _engine.Query(visual);

        Assert.Equal(2, result.CategoryCount);
        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void CardWithoutCategoryReturnsOneRow()
    {
        _engine.LoadSampleData();
        var t = _engine.Tables[0];
        var visual = new VisualSpec { Type = VisualType.Card, Values = [Field(t, "Product", Aggregation.CountDistinct)] };

        var row = Assert.Single(_engine.Query(visual).Rows);
        Assert.Equal(11.0, row[0]);
    }

    [Theory]
    [InlineData(DateGrain.Year, new[] { "2024", "2025" })]
    [InlineData(DateGrain.Quarter, new[] { "2024 Q1", "2024 Q4", "2025 Q1" })]
    [InlineData(DateGrain.Month, new[] { "2024-01", "2024-11", "2025-02" })]
    public void DateGrainBucketsChronologically(DateGrain grain, string[] expected)
    {
        _engine.ImportFile(WriteCsv("d.csv", "When,n\n2025-02-01,1\n2024-01-05,100\n2024-11-30,5\n2024-01-20,1\n"));
        var t = _engine.Tables[0];
        var visual = new VisualSpec
        {
            Type = VisualType.ColumnChart,
            Category = [new FieldRef(t.Fields[0], Aggregation.None) { Grain = grain }],
            Values = [Field(t, "n", Aggregation.Sum)],
        };

        var result = _engine.Query(visual);

        Assert.Equal(expected, result.Rows.Select(r => (string)r[0]!));
        Assert.Equal($"When ({grain})", result.Columns[0]);
    }

    [Fact]
    public void FieldsFromTwoTablesAreRejected()
    {
        _engine.ImportFile(WriteCsv("a.csv", "x\n1\n"));
        _engine.ImportFile(WriteCsv("b.csv", "y\n1\n"));
        var visual = new VisualSpec
        {
            Category = [Field(_engine.FindTable("a")!, "x")],
            Values = [Field(_engine.FindTable("b")!, "y", Aggregation.Sum)],
        };

        Assert.Throws<NotSupportedException>(() => _engine.Query(visual));
    }

    [Fact]
    public void IdentifiersWithQuotesAreEscaped()
    {
        _engine.ImportFile(WriteCsv("q.csv", "\"we\"\"ird\",n\nx,1\n"));
        var t = _engine.Tables[0];
        var visual = new VisualSpec { Category = [Field(t, "we\"ird")], Values = [Field(t, "n", Aggregation.Sum)] };

        Assert.Single(_engine.Query(visual).Rows);
    }

    [Fact]
    public void ProjectRoundTripsTablesAndReport()
    {
        _engine.LoadSampleData();
        _engine.ImportFile(WriteCsv("extra.csv", "k\n1\n2\n"));
        var sales = _engine.FindTable("Sales")!;
        var report = new ReportDocument();
        report.Pages[0].Visuals.Add(new VisualSpec
        {
            Type = VisualType.LineChart, X = 10, Y = 20,
            Category = [Field(sales, "Date")],
            Values = [Field(sales, "Revenue", Aggregation.Sum)],
        });
        var path = Path.Combine(_dir.FullName, "p" + ProjectFile.Extension);

        ProjectFile.Save(path, _engine, report);
        using var other = new DataEngine();
        var loaded = ProjectFile.Load(path, other);

        Assert.Equal(_engine.Tables.Select(t => (t.Name, t.RowCount)), other.Tables.Select(t => (t.Name, t.RowCount)));
        var v = Assert.Single(loaded.Pages[0].Visuals);
        Assert.Equal(VisualType.LineChart, v.Type);
        Assert.Equal(Aggregation.Sum, v.Values[0].Aggregation);
        Assert.InRange(other.Query(v).Rows.Count, 700, 731);
    }
}

public class ExcelImporterTests
{
    [Fact]
    public void InfersTypesSkipsBlankRowsAndNamesBlankHeaders()
    {
        var raw = new List<object?[]>
        {
            new object?[] { null, null, null },
            new object?[] { "Name", null, "When", null },
            new object?[] { "a", 1.0, new DateTime(2024, 1, 1), null },
            new object?[] { null, null, null, null },
            new object?[] { "b", "n/a", new DateTime(2024, 1, 2), null },
        };

        var sheet = ExcelImporter.FromRows("Sheet1", raw)!;

        Assert.Equal(["Name", "Column2", "When"], sheet.Columns.Select(c => c.Name));
        Assert.Equal([FieldKind.Text, FieldKind.Text, FieldKind.Date], sheet.Columns.Select(c => c.Kind));
        Assert.Equal(2, sheet.Rows.Count);
        Assert.Equal("1", sheet.Rows[0][1]);
        Assert.All(ExcelImporter.FromRows("Sheet1", raw, detectTypes: false)!.Columns, c => Assert.Equal(FieldKind.Text, c.Kind));
    }

    [Fact]
    public void EmptySheetIsSkipped() =>
        Assert.Null(ExcelImporter.FromRows("Empty", [new object?[] { null, "" }]));
}
