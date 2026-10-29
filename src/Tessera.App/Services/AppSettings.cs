using System.Text.Json;
using System.Text.Json.Serialization;
using Tessera.Core;

namespace Tessera.App.Services;

[JsonConverter(typeof(JsonStringEnumConverter<AppTheme>))]
public enum AppTheme { Light, Dark, System }

/// <summary>A Global choice that can defer to each report's Current file setting, as Power BI's Data Load page does.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FileDefault>))]
public enum FileDefault { Always, PerFile, Never }

/// <summary>Everything the Options dialog's Global pages control.</summary>
public sealed record Preferences
{
    public AppTheme Theme { get; init; } = AppTheme.Light;

    /// <summary>Infer column types in CSV and Excel files; never imports every column as text.</summary>
    public FileDefault TypeDetection { get; init; } = FileDefault.PerFile;

    /// <summary>Stored only: JuiceCI has no background previews.</summary>
    public FileDefault BackgroundData { get; init; } = FileDefault.PerFile;

    public bool AllExcelSheets { get; init; } = true;

    /// <summary>"Maximum number of simultaneous evaluations": DuckDB worker threads; 0 lets DuckDB decide.</summary>
    public int EngineThreads { get; init; }

    /// <summary>"Maximum memory used per simultaneous evaluation" in MB; the engine's limit is this times the threads. 0 lets DuckDB decide.</summary>
    public int EngineMemoryMb { get; init; }

    /// <summary>"Auto date/time for new files": off leaves new date axes ungrouped.</summary>
    public bool AutoDateTime { get; init; } = true;

    /// <summary>How a date field dropped on an axis is grouped at first.</summary>
    public DateGrain DefaultDateGrain { get; init; } = DateGrain.Month;

    public const int DefaultCacheMb = 4096;

    /// <summary>Data Cache "Maximum allowed (MB)": the most the engine may spill to disk.</summary>
    public int DataCacheMb { get; init; } = DefaultCacheMb;

    /// <summary>Natural language query cache "Maximum allowed (MB)". Stored only.</summary>
    public int QueryCacheMb { get; init; } = DefaultCacheMb;

    /// <summary>"Set ValueFilterBehavior to Independent for new files". Stored only.</summary>
    public bool ValueFilterIndependent { get; init; } = true;

    public bool SnapToGrid { get; init; } = true;
    public int GridSize { get; init; } = 8;

    /// <summary>Show guide lines, and snap to them, when a visual's edge or centre lines up with another's.</summary>
    public bool SmartGuides { get; init; } = true;

    /// <summary>Report view puts the page at the top of the canvas rather than in the middle.</summary>
    public bool AlignPageTop { get; init; } = true;

    /// <summary>Stored only: the Format pane has no subcategories to expand yet.</summary>
    public bool ExpandFormatSubcategories { get; init; }

    public bool ShowHomeOnStart { get; init; } = true;

    /// <summary>Right-clicking a visual offers to copy its name.</summary>
    public bool CopyObjectNames { get; init; }

    /// <summary>Culture name for number and date formatting; empty uses the system's.</summary>
    public string Culture { get; init; } = "";

    public int RecentLimit { get; init; } = 10;

    /// <summary>The other Global pages' settings, by <see cref="ViewModels.OptionsCatalog"/> key; a missing key keeps its default.</summary>
    public Dictionary<string, string> Options { get; init; } = [];
}

/// <summary>User preferences, kept in ~/.config/juiceci/settings.json.</summary>
public sealed class AppSettings
{
    public static string DefaultFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "juiceci");

    /// <summary>Where the app kept its files before it was renamed from Tessera.</summary>
    public static string LegacyFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "tessera");

    readonly string _path;

    public Preferences Values { get; set; } = new();

    /// <summary>The folder holding settings.json, and the Traces and AutoRecovery folders.</summary>
    public string Folder => Path.GetDirectoryName(_path)!;

    public AppSettings() : this(Path.Combine(DefaultFolder, "settings.json")) { }

    public AppSettings(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(_path)) Values = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(_path)) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Fall back to defaults rather than refusing to start.
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(Values));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
