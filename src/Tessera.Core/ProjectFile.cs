using System.IO.Compression;
using System.Text.Json;

namespace Tessera.Core;

/// <summary>
/// A .tsr project is a zip holding <c>project.json</c> (report layout + table list)
/// and one Parquet file per table, so it is self-contained like the data it was built from.
/// </summary>
public static class ProjectFile
{
    public const string Extension = ".tsr";
    const int FormatVersion = 1;

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    sealed class Manifest
    {
        public int Version { get; set; }
        public List<TableEntry> Tables { get; set; } = [];
        public ReportDocument Report { get; set; } = new();
        public SemanticModel? Model { get; set; }
    }

    sealed class TableEntry
    {
        public string Name { get; set; } = "";
        public string File { get; set; } = "";
    }

    public static void Save(string path, DataEngine engine, ReportDocument report)
    {
        var work = Directory.CreateTempSubdirectory("juiceci-save-");
        try
        {
            var model = engine.Model;
            var manifest = new Manifest
            {
                Version = FormatVersion, Report = report,
                Model = model.IsEmpty ? null : model,
            };
            Directory.CreateDirectory(Path.Combine(work.FullName, "data"));
            // Tables that only hold measures have no data of their own.
            var tables = engine.Tables.Where(t => t.Fields.Any(f => !f.IsMeasure)).ToList();
            for (var i = 0; i < tables.Count; i++)
            {
                var file = $"data/{i}.parquet";
                engine.ExportParquet(tables[i].Name, Path.Combine(work.FullName, file));
                manifest.Tables.Add(new TableEntry { Name = tables[i].Name, File = file });
            }
            File.WriteAllText(Path.Combine(work.FullName, "project.json"), JsonSerializer.Serialize(manifest, Json));

            // Write beside the target first so a failed save never clobbers the old file.
            var temp = path + ".tmp";
            File.Delete(temp);
            ZipFile.CreateFromDirectory(work.FullName, temp);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    /// <summary>Replaces the engine's tables with the project's and returns its report.</summary>
    public static ReportDocument Load(string path, DataEngine engine)
    {
        var work = Directory.CreateTempSubdirectory("juiceci-open-");
        try
        {
            ZipFile.ExtractToDirectory(path, work.FullName);
            var manifest = JsonSerializer.Deserialize<Manifest>(
                File.ReadAllText(Path.Combine(work.FullName, "project.json")), Json)
                ?? throw new InvalidDataException("project.json is empty.");
            if (manifest.Version > FormatVersion)
                throw new InvalidDataException($"Project format {manifest.Version} is newer than this app supports.");

            engine.Clear();
            foreach (var t in manifest.Tables)
            {
                var file = Path.GetFullPath(Path.Combine(work.FullName, t.File));
                if (!file.StartsWith(work.FullName + Path.DirectorySeparatorChar))
                    throw new InvalidDataException($"Bad table path in project: {t.File}");
                engine.ImportParquet(file, t.Name);
            }
            if (manifest.Model is not null) engine.Model = manifest.Model;
            return manifest.Report;
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }
}
