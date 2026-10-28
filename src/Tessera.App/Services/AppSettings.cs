using System.Text.Json;
using System.Text.Json.Serialization;
using Tessera.Core;

namespace Tessera.App.Services;

[JsonConverter(typeof(JsonStringEnumConverter<AppTheme>))]
public enum AppTheme { Light, Dark, System }

/// <summary>Everything the Options dialog's Global pages control.</summary>
public sealed record Preferences
{
    public AppTheme Theme { get; init; } = AppTheme.Light;

    /// <summary>Infer column types in CSV and Excel files; off imports every column as text.</summary>
    public bool DetectTypes { get; init; } = true;
    public bool AllExcelSheets { get; init; } = true;

    /// <summary>DuckDB worker threads; 0 lets DuckDB decide.</summary>
    public int EngineThreads { get; init; }

    /// <summary>DuckDB memory limit in MB; 0 lets DuckDB decide.</summary>
    public int EngineMemoryMb { get; init; }

    /// <summary>How a date field dropped on an axis is grouped at first.</summary>
    public DateGrain DefaultDateGrain { get; init; } = DateGrain.Month;

    public bool SnapToGrid { get; init; } = true;
    public int GridSize { get; init; } = 8;

    /// <summary>Culture name for number and date formatting; empty uses the system's.</summary>
    public string Culture { get; init; } = "";

    public int RecentLimit { get; init; } = 10;
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
