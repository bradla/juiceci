using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Tessera.Core;

namespace Tessera.Core.Tests;

public sealed class PbixTests : IDisposable
{
    static readonly string Sample = Path.Combine(AppContext.BaseDirectory, "Data", "ai-sample.pbix");

    readonly DataEngine _engine = new();
    readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("juiceci-test-");

    public void Dispose()
    {
        _engine.Dispose();
        _dir.Delete(recursive: true);
    }

    [Fact]
    public void ImportsEveryVisibleTableOfTheModel()
    {
        var names = _engine.ImportFile(Sample);

        // Calculated tables (the calendars, a what-if parameter) are included; Power BI's internal ones aren't.
        Assert.Equal(
            ["Accounts", "Campaigns", "Case Calendar", "Cases", "Contacts", "Industries", "Opportunities",
                "Opportunity Calendar", "Opportunity Forecast Adjustment", "Owners", "Products", "Territories"],
            names.Order());
        Assert.Equal(20000, _engine.FindTable("Opportunities")!.RowCount);
    }

    [Fact]
    public void ImportsRelationshipsAndMeasures()
    {
        _engine.ImportFile(Sample);
        var model = _engine.Model;

        Assert.Contains(new Relationship("Opportunities", "ProductSeq", "Products", "ProductSeq"), model.Relationships);
        Assert.Contains(model.Relationships, r => r is { FromTable: "Opportunities", ToTable: "Owners", IsActive: false });
        var close = model.FindMeasure("Close %")!;
        Assert.Equal("Opportunities", close.Table);
        Assert.Equal("0.0%;-0.0%;0.0%", close.FormatString);
        var field = _engine.FindTable("Opportunities")!.Fields.Single(f => f.Name == "Close %");
        Assert.True(field.IsMeasure);
    }

    FieldRef Column(string table, string column, Aggregation agg = Aggregation.None) =>
        new(_engine.FindTable(table)!.Fields.Single(f => f.Name == column && !f.IsMeasure), agg);

    FieldRef MeasureRef(string table, string name) =>
        new(_engine.FindTable(table)!.Fields.Single(f => f.Name == name && f.IsMeasure), Aggregation.None);

    [Fact]
    public void MeasuresAreEvaluatedAcrossRelationships()
    {
        _engine.ImportFile(Sample);

        // The numbers Power BI shows on the sample's Key Influencers page.
        var close = _engine.Query(new VisualSpec
        {
            Type = VisualType.BarChart,
            Category = [Column("Products", "Product Category")],
            Values = [MeasureRef("Opportunities", "Close %")],
        });
        Assert.Equal(["Product Category", "Close %"], close.Columns);
        Assert.Equal("0.0%;-0.0%;0.0%", close.FormatOf(1));
        Assert.Equal(["Computers", "Furniture", "Software", "Electronics"], close.Rows.Select(r => (string)r[0]!));
        Assert.Equal("75.6%", Sql.FormatNumber((double)close.Rows[0][1]!, close.FormatOf(1), compact: true));

        var won = _engine.Query(new VisualSpec
        {
            Type = VisualType.BarChart,
            Category = [Column("Products", "Product")],
            Values = [MeasureRef("Opportunities", "Revenue Won")],
        });
        Assert.Equal(("Design app", 10_130_200.0), ((string)won.Rows[0][0]!, (double)won.Rows[0][1]!));
        Assert.Equal("$10.13M", Sql.FormatNumber(10_130_200, won.FormatOf(1), compact: true));

        // Owners is reached through Accounts, because the direct Opportunities → Owners link is inactive.
        var byManager = _engine.Query(new VisualSpec
        {
            Category = [Column("Owners", "Manager")],
            Values = [MeasureRef("Opportunities", "Revenue Won")],
        });
        Assert.Equal(2, byManager.Rows.Count);
        Assert.Equal(TotalRevenueWon(), byManager.Rows.Sum(r => (double)r[1]!), 3);

        // A plain summarized column works the same way.
        var byRegion = _engine.Query(new VisualSpec
        {
            Type = VisualType.BarChart,
            Category = [Column("Accounts", "Region")],
            Values = [Column("Opportunities", "Value", Aggregation.Sum)],
        });
        Assert.Equal(4, byRegion.Rows.Count);
    }

    double TotalRevenueWon() => (double)_engine.Query(new VisualSpec
    {
        Type = VisualType.Card,
        Values = [MeasureRef("Opportunities", "Revenue Won")],
    }).Rows[0][0]!;

    [Fact]
    public void ReadsTheReportPages()
    {
        _engine.ImportFile(Sample);
        var doc = PbixReportImporter.Read(Sample, _engine.Tables)!;

        Assert.Equal(["Key Influencers", "Decomposition Tree", "Anomaly Detection"], doc.Pages.Select(p => p.Name));
        var page = doc.Pages[0];
        Assert.Equal((1280, 720), (page.Width, page.Height));   // scaled from 1920 × 1080

        var close = page.Visuals.Single(v => v.Values.Any(f => f.Column == "Close %"));
        Assert.Equal(VisualType.BarChart, close.Type);
        Assert.Equal("Product Category", Assert.Single(close.Category).Column);
        Assert.Equal((15, 99), (close.X, close.Y));
        Assert.All(page.Visuals.Where(v => v.Type.ShowsData() && !v.Type.IsAnalysis()), v => Assert.Null(Record.Exception(() => _engine.Query(v))));

        Assert.DoesNotContain(page.Visuals, v => v.Type == VisualType.Unsupported);
        Assert.Equal("Slicer", doc.Pages[2].Visuals.Single(v => v.Type == VisualType.Unsupported).Style!.Text);
        var logo = page.Visuals.Single(v => v.Type == VisualType.Text && v.Style!.Text == "Contoso").Style!;
        Assert.Equal(("#FFFFFF", true), (logo.Foreground, logo.Bold));
        Assert.NotNull(page.Visuals.Single(v => v.Type == VisualType.Image).Style!.Image);
        Assert.Single(page.Visuals, v => v.Type == VisualType.PageNavigator);
        // Header bar: theme colour 3 (the theme's second data colour), 50% darker.
        Assert.Contains(page.Visuals, v => v.Type == VisualType.Shape && v.Style!.Background == "#1A2E4B");
        // Shapes and text are drawn in Power BI's stacking order.
        var order = page.Visuals.Select(v => v.Type).ToList();
        Assert.True(order.IndexOf(VisualType.Shape) < order.IndexOf(VisualType.Text));

        var line = doc.Pages[2].Visuals.Single(v => v.Type == VisualType.LineChart);
        Assert.Equal(("Opportunity Calendar", "Date"), (line.Category[0].Table, line.Category[0].Column));
        Assert.NotEmpty(_engine.Query(line).Rows);
    }

    [Fact]
    public void KeyInfluencersFindWhatMakesTheTargetLikely()
    {
        _engine.ImportFile(Sample);
        var doc = PbixReportImporter.Read(Sample, _engine.Tables)!;
        var visual = doc.Pages[0].Visuals.Single(v => v.Type == VisualType.KeyInfluencers);
        Assert.Equal(("Status", "Won"), (visual.Values[0].Column, visual.TargetValue));
        Assert.Contains(visual.Category, c => c.Column == "Discount" && c.Aggregation == Aggregation.None);

        // Power BI's own figures for this page: Molly Clark 1.52x, Tablets 1.50x, Low, Spencer 1.36x.
        var analysis = _engine.KeyInfluencers(visual, doc.Pages[0].Filters);
        Assert.Equal(17533, analysis.Rows);
        Assert.Equal("Discount goes up 0.02", analysis.Influencers[0].Condition);
        double Ratio(string condition) => Math.Round(analysis.Influencers.Single(i => i.Condition == condition).Ratio, 2);
        Assert.Equal(1.52, Ratio("Sales Owner is Molly Clark"));
        Assert.Equal(1.50, Ratio("Product is Tablets"));
        Assert.Equal(1.35, Ratio("Manager is Low, Spencer"));
        Assert.DoesNotContain(analysis.Influencers, i => i.Condition == "Product is Scanners");

        // Asking about the other outcome turns the findings around.
        visual.TargetValue = "Lost";
        var lost = _engine.KeyInfluencers(visual, doc.Pages[0].Filters);
        Assert.Contains(lost.Influencers, i => i.Condition == "Product is Scanners");
        Assert.Equal("Discount goes down 0.02", lost.Influencers.Single(i => i.Breakdown.Name == "Discount").Condition);
    }

    [Fact]
    public void DecompositionTreeFollowsTheSavedPath()
    {
        _engine.ImportFile(Sample);
        var doc = PbixReportImporter.Read(Sample, _engine.Tables)!;
        var page = doc.Pages[1];
        var tree = page.Visuals.Single(v => v.Type == VisualType.DecompositionTree);
        Assert.Equal("Sales Opportunity", tree.Values[0].DisplayName);
        Assert.Equal(["Region", "Product Category", "Product", "Territory", "Sales Owner", "Industry"], tree.Category.Select(c => c.Column));

        var d = _engine.Decompose(tree, page.Filters);
        Assert.Equal(29_621_000, d.Total);
        Assert.Equal([21_811_300, 9_594_700, 7_599_400, 2_986_300, 1_162_100, 152_900], d.Levels.Select(l => l.Nodes[l.Selected].Amount));
        Assert.Equal(["North America", "LATAM", "EMEA", "APAC"], d.Levels[0].Nodes.Select(n => n.Label));
        Assert.Equal((10, 7), (d.Levels[5].Nodes.Count, d.Levels[5].Hidden));

        // Closing a level drops everything after it.
        tree.Drill = ["LATAM"];
        d = _engine.Decompose(tree, page.Filters);
        Assert.Equal(2, d.Levels.Count);
        Assert.Equal(-1, d.Levels[1].Selected);
        Assert.Equal(3_652_200, d.Levels[1].Parent);
    }

    [Fact]
    public void AdvancedFiltersAndColumnFormatsComeAcross()
    {
        _engine.ImportFile(Sample);
        var doc = PbixReportImporter.Read(Sample, _engine.Tables)!;
        var page = doc.Pages[1];
        var chart = page.Visuals.Single(v => v.Category.Any(c => c.Column == "Type"));
        Assert.Equal("Campaign Type", chart.Category[0].DisplayName);
        Assert.Equal("Sales Opportunity", chart.Values[0].DisplayName);
        var filter = Assert.Single(chart.Filters);
        Assert.Equal([new FilterRule(FilterOperator.IsNotBlank)], filter.Rules);

        var result = _engine.Query(chart, 10, page.Filters);
        Assert.Equal(["Email", "Phone", "Tradeshow", "Online"], result.Rows.Select(r => (string?)r[0]));
        // Value is a currency column, so its sum is labelled in dollars, in the unit of the largest bar.
        var label = Sql.UnitFormatter(result.Rows.Select(r => (double)r[1]!), result.FormatOf(1));
        Assert.Equal(["$4.0M", "$2.7M", "$1.3M", "$0.3M"], result.Rows.Select(r => label((double)r[1]!)));
    }

    [Theory]
    [InlineData(FilterOperator.Contains, "app", 2)]
    [InlineData(FilterOperator.StartsWith, "DESIGN", 1)]
    [InlineData(FilterOperator.DoesNotContain, "s", 2)]
    [InlineData(FilterOperator.Is, "Tablets", 1)]
    public void FilterRulesCompareText(FilterOperator op, string value, int products)
    {
        _engine.ImportFile(Sample);
        var product = new FieldRef(_engine.FindTable("Products")!.Fields.Single(f => f.Name == "Product"), Aggregation.None);
        var visual = new VisualSpec
        {
            Type = VisualType.Table, Category = [product],
            Filters = [new FieldFilter { Field = product, Rules = [new FilterRule(op, value)] }],
        };
        Assert.Equal(products, _engine.Query(visual).Rows.Count);
    }

    [Fact]
    public void ReadsTheOlderSingleFileLayout()
    {
        var path = Path.Combine(_dir.FullName, "legacy.pbix");
        File.Copy(Sample, path);
        var config = JsonSerializer.Serialize(new
        {
            name = "v1",
            singleVisual = new
            {
                visualType = "clusteredColumnChart",
                projections = new { Category = new[] { new { queryRef = "p.Product Category" } }, Y = new[] { new { queryRef = "Sum(o.Value)" } } },
                prototypeQuery = new
                {
                    From = new[] { new { Name = "p", Entity = "Products" }, new { Name = "o", Entity = "Opportunities" } },
                    Select = new object[]
                    {
                        new { Column = new { Expression = new { SourceRef = new { Source = "p" } }, Property = "Product Category" }, Name = "p.Product Category" },
                        new
                        {
                            Aggregation = new { Expression = new { Column = new { Expression = new { SourceRef = new { Source = "o" } }, Property = "Value" } }, Function = 0 },
                            Name = "Sum(o.Value)",
                        },
                    },
                },
            },
        });
        var layout = JsonSerializer.Serialize(new
        {
            sections = new[]
            {
                new { displayName = "Second", ordinal = 1, width = 1280, height = 720, visualContainers = Array.Empty<object>() },
                new
                {
                    displayName = "First", ordinal = 0, width = 1280, height = 720,
                    visualContainers = new object[] { new { x = 10, y = 20, z = 0, width = 400, height = 300, config } },
                },
            },
        });
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("Report/definition/")).ToList()) e.Delete();
            using var w = zip.CreateEntry("Report/Layout").Open();
            w.Write(Encoding.Unicode.GetBytes(layout));
        }

        _engine.ImportFile(path);
        var doc = PbixReportImporter.Read(path, _engine.Tables)!;

        Assert.Equal(["First", "Second"], doc.Pages.Select(p => p.Name));
        var chart = Assert.Single(doc.Pages[0].Visuals);
        Assert.Equal(VisualType.ColumnChart, chart.Type);
        Assert.Equal(Aggregation.Sum, Assert.Single(chart.Values).Aggregation);
        Assert.Equal(4, _engine.Query(chart).Rows.Count);
    }

    [Fact]
    public void ProjectKeepsTheModel()
    {
        _engine.ImportFile(Sample);
        var path = Path.Combine(_dir.FullName, "p" + ProjectFile.Extension);
        ProjectFile.Save(path, _engine, new ReportDocument());

        using var other = new DataEngine();
        ProjectFile.Load(path, other);

        Assert.Equal(_engine.Model.Measures.Count, other.Model.Measures.Count);
        Assert.Equal(_engine.Model.Relationships, other.Model.Relationships);
        Assert.Contains(other.FindTable("Opportunities")!.Fields, f => f.IsMeasure && f.Name == "Revenue Won");
    }

    [Fact]
    public void DecodesTypesDictionariesAndNulls()
    {
        _engine.ImportFile(Sample);
        var opportunities = _engine.FindTable("Opportunities")!;
        FieldKind Kind(string column) => opportunities.Fields.Single(f => f.Name == column).Kind;

        Assert.Equal(FieldKind.Date, Kind("CloseDate"));
        Assert.Equal(FieldKind.Boolean, Kind("Decision Maker Identified"));
        Assert.Equal(FieldKind.Number, Kind("Value"));

        var owners = _engine.Preview("Owners");
        Assert.Equal(13, owners.Rows.Count);
        Assert.Equal(["Molly Clark", "Low, Spencer", 1.0], owners.Rows[0]);

        var accounts = _engine.FindTable("Accounts")!;
        var visual = new VisualSpec
        {
            Type = VisualType.Card,
            Values = [new FieldRef(accounts.Fields.Single(f => f.Name == "Postal Code"), Aggregation.Count)],
        };
        Assert.Equal(283.0, _engine.Query(visual).Rows[0][0]);   // 300 accounts, 17 without a postal code
    }

    [Fact]
    public void LiveConnectedReportExplainsThereIsNoData()
    {
        var path = Path.Combine(_dir.FullName, "live.pbix");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            zip.CreateEntry("Connections");

        var e = Assert.Throws<InvalidDataException>(() => _engine.ImportFile(path));
        Assert.Contains("connects live", e.Message);
        Assert.Empty(_engine.Tables);
    }

    [Fact]
    public void CorruptModelLeavesNoTablesBehind()
    {
        var path = Path.Combine(_dir.FullName, "bad.pbix");
        File.Copy(Sample, path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("DataModel")!;
            var bytes = new byte[entry.Length];
            using (var s = entry.Open()) s.ReadExactly(bytes);
            entry.Delete();
            bytes[bytes.Length / 2] ^= 0xff;   // flip a byte deep inside the compressed stream
            using var w = zip.CreateEntry("DataModel").Open();
            w.Write(bytes);
        }

        Assert.ThrowsAny<Exception>(() => _engine.ImportFile(path));
        Assert.Empty(_engine.Tables);
    }

    [Fact]
    public void ReportSettingsComeAcross()
    {
        _engine.ImportFile(Sample);
        var settings = PbixReportImporter.Read(Sample, _engine.Tables)!.Settings!;
        Assert.Equal("true", settings[FileSettings.CrossFilterDefault]);
        Assert.Equal("true", settings[FileSettings.ChangeFilterTypes]);
        Assert.Equal("true", settings[FileSettings.ModernHeader]);
        Assert.Equal("0", settings[FileSettings.ExportData]);
        Assert.Equal("false", settings[FileSettings.DisableCrossHighlight]);
        Assert.Equal("0", settings[FileSettings.FilterApply]);
        Assert.Equal("No query limits", settings[FileSettings.QueryLimit]);
        Assert.Equal("true", settings[FileSettings.TypeDetection]);
        Assert.Equal("true", settings[FileSettings.ImportRelationships]);
    }

    [Fact]
    public void ThemesComeAcrossAndRoundTrip()
    {
        _engine.ImportFile(Sample);
        var theme = PbixReportImporter.Read(Sample, _engine.Tables)!.Theme!;
        Assert.NotEmpty(theme.DataColors);
        Assert.All(theme.DataColors, c => Assert.Matches("^#[0-9A-F]{6}$", c));

        var midnight = ReportTheme.BuiltIn.Single(t => t.Name == "Midnight");
        var back = ReportTheme.FromJson(midnight.ToJson());
        Assert.Equal(midnight.DataColors, back.DataColors);
        Assert.Equal((midnight.Background, midnight.Foreground, midnight.PageBackground), (back.Background, back.Foreground, back.PageBackground));
        Assert.Throws<FormatException>(() => ReportTheme.FromJson("{\"name\":\"Empty\"}"));
    }
}
