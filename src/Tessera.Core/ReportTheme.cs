using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tessera.Core;

/// <summary>
/// A report theme in Power BI's theme-file terms: data colours for series, and the background and
/// foreground of visuals. Imported, exported and customized from the Design tab.
/// </summary>
public sealed class ReportTheme
{
    public string Name { get; set; } = "Theme";

    /// <summary>Series colours, first to last.</summary>
    public List<string> DataColors { get; set; } = [];

    /// <summary>Behind each visual.</summary>
    public string Background { get; set; } = "#FFFFFF";

    /// <summary>Titles, labels and axis text.</summary>
    public string Foreground { get; set; } = "#252423";

    /// <summary>The page's colour where a page doesn't set its own; null leaves it white.</summary>
    public string? PageBackground { get; set; }

    public ReportTheme Clone() => new()
    {
        Name = Name, DataColors = [.. DataColors], Background = Background, Foreground = Foreground, PageBackground = PageBackground,
    };

    /// <summary>The themes shown in the Design tab's gallery, Power BI's default first.</summary>
    public static IReadOnlyList<ReportTheme> BuiltIn { get; } =
    [
        new() { Name = "Default", DataColors = ["#118DFF", "#12239E", "#E66C37", "#6B007B", "#E044A7", "#744EC2", "#D9B300", "#D64550"] },
        new() { Name = "JuiceCI", DataColors = ["#0E7C66", "#E0A526", "#3B6FB6", "#D2553F", "#7A5BA6", "#4FA3A5", "#B5651D", "#8C8C3A"] },
        new()
        {
            Name = "Twilight", DataColors = ["#7FBFFF", "#C77DFF", "#FF8FAB", "#FFD166", "#5EEAD4", "#A5B4FC", "#F9A8D4", "#FDBA74"],
            Background = "#3B2A6B", Foreground = "#F4F0FF", PageBackground = "#2A1D52",
        },
        new()
        {
            Name = "Midnight", DataColors = ["#4CC9F0", "#F72585", "#B5179E", "#7209B7", "#4361EE", "#FFD60A", "#06D6A0", "#FB8500"],
            Background = "#1B1B1F", Foreground = "#F2F2F2", PageBackground = "#111114",
        },
        new() { Name = "Blossom", DataColors = ["#E15B64", "#F47E60", "#F8B26A", "#ABBD81", "#849B87", "#E8A0BF", "#C05780", "#6C8EBF"], PageBackground = "#F7F2F2" },
        new() { Name = "Harvest", DataColors = ["#8C6A43", "#C8963E", "#D96C3F", "#6B8E4E", "#3F6E8C", "#B5A642", "#9C4F46", "#5F5A8C"], PageBackground = "#F6F2EA" },
        new() { Name = "Tidal", DataColors = ["#094782", "#0B72D7", "#098BF5", "#54B5FB", "#71C0A7", "#57B956", "#478F48", "#326633"] },
        new() { Name = "Temperature", DataColors = ["#106981", "#D43D51", "#E9A06A", "#5FA8C5", "#F4D06F", "#A0C27A", "#8A5A83", "#E27D60"] },
    ];

    /// <summary>Reads a Power BI theme file: name, dataColors, background, foreground (and the page colour, if set).</summary>
    public static ReportTheme FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("A theme file is a JSON object.");
        var theme = new ReportTheme { Name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "Theme" : "Theme" };
        if (root.TryGetProperty("dataColors", out var colors) && colors.ValueKind == JsonValueKind.Array)
            theme.DataColors = colors.EnumerateArray().Select(c => Hex(c.GetString())).OfType<string>().ToList();
        if (theme.DataColors.Count == 0) throw new FormatException("The theme has no dataColors.");
        if (root.TryGetProperty("background", out var bg) && Hex(bg.GetString()) is { } b) theme.Background = b;
        if (root.TryGetProperty("foreground", out var fg) && Hex(fg.GetString()) is { } f) theme.Foreground = f;
        if (root.TryGetProperty("visualStyles", out var styles) && styles.TryGetProperty("page", out var page)
            && page.TryGetProperty("*", out var all) && all.TryGetProperty("background", out var list)
            && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0
            && list[0].TryGetProperty("color", out var color) && color.TryGetProperty("solid", out var solid)
            && solid.TryGetProperty("color", out var hex))
            theme.PageBackground = Hex(hex.GetString());
        return theme;
    }

    /// <summary>Writes the theme as a Power BI theme file, which Power BI Desktop can import too.</summary>
    public string ToJson()
    {
        var json = new JsonObject
        {
            ["name"] = Name,
            ["dataColors"] = new JsonArray(DataColors.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
            ["background"] = Background,
            ["foreground"] = Foreground,
        };
        if (PageBackground is { } page)
            json["visualStyles"] = new JsonObject
            {
                ["page"] = new JsonObject
                {
                    ["*"] = new JsonObject
                    {
                        ["background"] = new JsonArray(new JsonObject
                        {
                            ["color"] = new JsonObject { ["solid"] = new JsonObject { ["color"] = page } },
                        }),
                    },
                },
            };
        return json.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    static string? Hex(string? value) =>
        value is { Length: 7 } v && v[0] == '#' && v[1..].All(Uri.IsHexDigit) ? v.ToUpperInvariant() : null;
}
