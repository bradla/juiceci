namespace Tessera.App.Services;

/// <summary>
/// Diagnostics › Crash Dump Collection: when on, an unhandled exception is written to
/// the traces folder before the process ends.
/// </summary>
public static class CrashLog
{
    static bool _installed;

    /// <summary>Where crash logs go; null turns collection off.</summary>
    public static string? Folder { get; set; }

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => Write(e.Exception);
    }

    /// <summary>Returns the log's path, or null when collection is off or the write failed.</summary>
    public static string? Write(Exception? error)
    {
        if (Folder is null || error is null) return null;
        try
        {
            Directory.CreateDirectory(Folder);
            var path = Path.Combine(Folder, $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
            File.WriteAllText(path, $"{DateTime.Now:O}{Environment.NewLine}{error}{Environment.NewLine}");
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
