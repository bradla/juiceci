using System.Collections.ObjectModel;
using System.Text.Json;

namespace Tessera.App.Services;

/// <summary>Most-recently-used projects, kept in ~/.config/juiceci/recent.json.</summary>
public sealed class RecentFiles
{
    readonly string _path;
    int _max = 10;

    /// <summary>How many reports the list keeps; lowering it drops the oldest.</summary>
    public int Max
    {
        get => _max;
        set
        {
            _max = Math.Max(1, value);
            if (Items.Count <= _max) return;
            while (Items.Count > _max) Items.RemoveAt(Items.Count - 1);
            Save();
        }
    }

    public ObservableCollection<RecentFile> Items { get; } = [];

    public RecentFiles() : this(Path.Combine(AppSettings.DefaultFolder, "recent.json"),
        Path.Combine(AppSettings.LegacyFolder, "recent.json")) { }

    public RecentFiles(string path, string? legacyPath = null)
    {
        _path = path;
        var source = !File.Exists(_path) && legacyPath is not null && File.Exists(legacyPath) ? legacyPath : _path;
        try
        {
            if (!File.Exists(source)) return;
            foreach (var p in JsonSerializer.Deserialize<List<string>>(File.ReadAllText(source)) ?? [])
                if (File.Exists(p)) Items.Add(new RecentFile(p));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A broken history file shouldn't stop the app from starting.
        }
    }

    public void Add(string path)
    {
        foreach (var existing in Items.Where(i => i.Path == path).ToList()) Items.Remove(existing);
        Items.Insert(0, new RecentFile(path));
        while (Items.Count > Max) Items.RemoveAt(Items.Count - 1);
        Save();
    }

    public void Clear()
    {
        Items.Clear();
        Save();
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(Items.Select(i => i.Path)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

public sealed record RecentFile(string Path)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    public DateTime LastWrite => File.Exists(Path) ? File.GetLastWriteTime(Path) : DateTime.MinValue;
    public string Modified => File.Exists(Path) ? LastWrite.ToString("g") : "";
}
