using Tessera.Core;

namespace Tessera.Core.Tests;

public sealed class DaxTests : IDisposable
{
    readonly DataEngine _engine = new();
    readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("juiceci-test-");

    public DaxTests()
    {
        Import("Sales", "Product,Status,Amount\n1,Won,10\n1,Lost,5\n2,Won,20\n2,Won,\n3,Open,7\n");
        Import("Products", "Product,Name,Category\n1,Desk,Furniture\n2,Chair,Furniture\n3,Laptop,Computers\n");
        _engine.Model = new SemanticModel
        {
            Relationships = [new Relationship("Sales", "Product", "Products", "Product")],
            Measures =
            [
                new("Sales", "Total", "SUM ( Sales[Amount] )"),
                new("Sales", "Won", "CALCULATE(SUMX(Sales, Sales[Amount]), FILTER(Sales, Sales[Status] = \"Won\"))"),
                new("Sales", "Won Count", "COUNTAX(FILTER(KEEPFILTERS(Sales), Sales[Status] = \"Won\"), Sales[Status])"),
                new("Sales", "Lost Count", "CALCULATE(COUNTROWS('Sales'), 'Sales'[Status] = \"Lost\")"),
                new("Sales", "Win Rate", "[Won Count] / ([Won Count] + [Lost Count]) -- a comment"),
                new("Sales", "Share", "VAR w = [Won] VAR t = [Total] RETURN DIVIDE(w, t, 0)"),
                new("Sales", "Big", "CALCULATE([Total], Sales[Amount] >= 10 && NOT(Sales[Status] IN { \"Open\" }))"),
                new("Sales", "Label", "IF([Total] > 20, \"high\", \"low\")"),
                new("Sales", "All", "CALCULATE([Total], ALL(Sales))"),
                new("Sales", "Loop", "[Loop] + 1"),
            ],
        };
    }

    public void Dispose()
    {
        _engine.Dispose();
        _dir.Delete(recursive: true);
    }

    void Import(string name, string csv)
    {
        var path = Path.Combine(_dir.FullName, name + ".csv");
        File.WriteAllText(path, csv);
        _engine.ImportFile(path);
    }

    FieldRef Measure(string name) => new(_engine.FindTable("Sales")!.Fields.Single(f => f.Name == name && f.IsMeasure), Aggregation.None);

    object? Total(string measure) => _engine.Query(new VisualSpec { Type = VisualType.Card, Values = [Measure(measure)] }).Rows[0][0];

    Dictionary<string, object?> ByCategory(string measure) => _engine.Query(new VisualSpec
    {
        Type = VisualType.Table,
        Category = [new FieldRef(_engine.FindTable("Products")!.Fields.Single(f => f.Name == "Category"), Aggregation.None)],
        Values = [Measure(measure)],
    }).Rows.ToDictionary(r => (string)r[0]!, r => r[1]);

    [Theory]
    [InlineData("Total", 42.0)]
    [InlineData("Won", 30.0)]
    [InlineData("Won Count", 3.0)]
    [InlineData("Lost Count", 1.0)]
    [InlineData("Win Rate", 0.75)]
    [InlineData("Big", 30.0)]
    public void EvaluatesCommonPatterns(string measure, double expected) =>
        Assert.Equal(expected, (double)Total(measure)!, 6);

    [Fact]
    public void VariablesAndDivideWork() => Assert.Equal(30.0 / 42, (double)Total("Share")!, 6);

    [Fact]
    public void MeasuresFollowTheVisualsGrouping()
    {
        var won = ByCategory("Won");
        Assert.Equal(30.0, won["Furniture"]);
        Assert.Null(won["Computers"]);   // no won deals: blank, as in Power BI
    }

    [Fact]
    public void TextResultsAreAllowed() => Assert.Equal("high", Total("Label"));

    [Fact]
    public void FiltersFollowRelationshipsAndCombine()
    {
        FieldRef Col(string table, string name) => new(_engine.FindTable(table)!.Fields.Single(f => f.Name == name), Aggregation.None);
        var visual = new VisualSpec { Type = VisualType.Card, Values = [Measure("Total")] };
        double Run(params FieldFilter[] page) => (double)_engine.Query(visual, pageFilters: page).Rows[0][0]!;

        // A filter on the dimension narrows the fact rows through the relationship.
        Assert.Equal(35.0, Run(new FieldFilter { Field = Col("Products", "Category"), Values = ["Furniture"] }));
        // Page and visual filters both apply; an empty filter is "(All)".
        visual.Filters = [new FieldFilter { Field = Col("Sales", "Status"), Values = ["Won"] }];
        Assert.Equal(30.0, Run(new FieldFilter { Field = Col("Products", "Category"), Values = ["Furniture"] },
            new FieldFilter { Field = Col("Products", "Name") }));
        // Blank is a value of its own.
        visual.Filters = [new FieldFilter { Field = Col("Sales", "Amount"), Values = [null] }];
        Assert.Null(_engine.Query(visual).Rows[0][0]);   // only the blank-amount row is left, and its sum is blank
        Assert.Equal(["1", "2", "3"], _engine.DistinctValues("Products", "Product").Select(x => x![..1]));
    }

    [Fact]
    public void SortByColumnOrdersCategories()
    {
        Import("Months", "Month,MonthNo\nJan,1\nFeb,2\nMar,3\n");
        _engine.Model = new SemanticModel { SortRules = [new SortRule("Months", "Month", "MonthNo")] };
        var month = new FieldRef(_engine.FindTable("Months")!.Fields.Single(f => f.Name == "Month"), Aggregation.None);
        var no = new FieldRef(_engine.FindTable("Months")!.Fields.Single(f => f.Name == "MonthNo"), Aggregation.Sum);

        foreach (var type in new[] { VisualType.ColumnChart, VisualType.LineChart, VisualType.Table })
        {
            var visual = new VisualSpec { Type = type, Category = [month], Values = type == VisualType.Table ? [] : [no] };
            Assert.Equal(["Jan", "Feb", "Mar"], _engine.Query(visual).Rows.Select(r => (string)r[0]!));
        }
    }

    [Theory]
    [InlineData("All", "ALL")]
    [InlineData("Loop", "refers to itself")]
    public void UnsupportedDaxExplainsItself(string measure, string message)
    {
        var e = Assert.Throws<NotSupportedException>(() => Total(measure));
        Assert.Contains(message, e.Message);
    }
}
