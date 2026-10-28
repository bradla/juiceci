namespace Tessera.Core;

/// <summary>Deterministic, made-up sales data for trying the app without a file.</summary>
public static class SampleData
{
    public sealed record Sale(DateTime Date, string Region, string Channel, string Segment, string Category,
        string Product, double Units, double UnitPrice, double Revenue, double Cost);

    public static readonly IReadOnlyList<ColumnSchema> Columns =
    [
        new("Date", FieldKind.Date), new("Region", FieldKind.Text), new("Channel", FieldKind.Text),
        new("Segment", FieldKind.Text), new("Category", FieldKind.Text), new("Product", FieldKind.Text),
        new("Units", FieldKind.Number), new("Unit Price", FieldKind.Number), new("Revenue", FieldKind.Number),
        new("Cost", FieldKind.Number),
    ];

    static readonly (string Name, string Category, double Price, double CostRatio)[] Products =
    [
        ("Trail Runner", "Footwear", 120, 0.55), ("City Sneaker", "Footwear", 85, 0.5),
        ("Hiking Boot", "Footwear", 160, 0.6), ("Rain Shell", "Apparel", 140, 0.45),
        ("Fleece Jacket", "Apparel", 95, 0.4), ("Base Layer", "Apparel", 45, 0.35),
        ("Day Pack", "Gear", 70, 0.5), ("Tent 2P", "Gear", 320, 0.62), ("Headlamp", "Gear", 35, 0.3),
        ("Water Bottle", "Accessories", 20, 0.25), ("Cap", "Accessories", 25, 0.2),
    ];

    static readonly string[] Regions = ["North", "South", "East", "West", "Central"];
    static readonly string[] Channels = ["Online", "Retail", "Partner"];
    static readonly string[] Segments = ["Consumer", "Small Business", "Enterprise", "Education"];

    public static IEnumerable<Sale> Generate(int rows = 5000, int seed = 42)
    {
        var rng = new Random(seed);
        var start = new DateTime(2024, 1, 1);
        for (var i = 0; i < rows; i++)
        {
            var date = start.AddDays(rng.Next(731));
            var p = Products[rng.Next(Products.Length)];
            // Gentle growth over time plus a Q4 bump, so line charts have shape.
            var season = date.Month >= 10 ? 1.4 : 1.0;
            var growth = 1 + (date - start).TotalDays / 731 * 0.3;
            var units = Math.Max(1, Math.Round(rng.Next(1, 20) * season * growth));
            var discount = rng.Next(4) * 0.05;
            var revenue = Math.Round(units * p.Price * (1 - discount), 2);
            var cost = Math.Round(units * p.Price * p.CostRatio, 2);
            yield return new Sale(date, Regions[rng.Next(Regions.Length)], Channels[rng.Next(Channels.Length)],
                Segments[rng.Next(Segments.Length)], p.Category, p.Name, units, p.Price, revenue, cost);
        }
    }
}
