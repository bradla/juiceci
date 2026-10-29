using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tessera.App.Services;
using Tessera.Core;
using F = Tessera.Core.FileSettings;

namespace Tessera.App.ViewModels;

/// <summary>A setting on an Options page, stored as text under <c>Key</c>.</summary>
public abstract record OptionDef;

/// <param name="Parent">Another checkbox's key: this one is indented under it and only enabled while it's ticked.</param>
/// <param name="Locked">Shown but greyed out, for settings that don't apply here.</param>
public sealed record CheckDef(string Key, string Label, bool Default = false, string? Tip = null, string? Parent = null, bool Locked = false)
    : OptionDef;

/// <summary>Radio buttons; stores the chosen index.</summary>
/// <param name="Tips">An info icon's text per option, where one has it.</param>
public sealed record RadioDef(string Key, IReadOnlyList<string> Labels, int Default = 0, IReadOnlyList<string?>? Tips = null) : OptionDef;

/// <summary>A drop-down whose items are worked out when the page opens; stores the chosen item.</summary>
/// <param name="Label">Text above the drop-down; null for none.</param>
public sealed record ListDef(string Key, string? Label, Func<IReadOnlyList<string>> Items, string? Default = null) : OptionDef;

public sealed record NumberDef(string Key, string Label, int Default, int Min, int Max) : OptionDef;

public sealed record TextDef(string Key, string Label, string Default = "", bool Multiline = false) : OptionDef;

public sealed record NoteDef(string Text) : OptionDef;

/// <summary>A button, or a link when <paramref name="IsLink"/>, that does something now rather than storing a choice.</summary>
/// <param name="Run">Given the page's working settings, so it can act on choices not yet saved.</param>
public sealed record ActionDef(string Label, Action<IReadOnlyDictionary<string, string>> Run, bool IsLink = false) : OptionDef;

/// <summary>A checkbox with a number inside its sentence: "Store … every [10] minutes".</summary>
public sealed record CheckNumberDef(string Key, string Before, string NumberKey, string After, bool Default, int DefaultNumber, int Min, int Max)
    : OptionDef;

/// <summary>A folder path with a Browse button; stores nothing until changed, so the default can follow the settings folder.</summary>
public sealed record FolderDef(string Key, string Label, Func<string> Default) : OptionDef;

/// <summary>An on/off setting shown as a status line and an "Enable Now" / "Disable" button.</summary>
public sealed record ToggleDef(string Key, string OnText, string OffText, string EnableLabel, string DisableLabel) : OptionDef;

/// <summary>A smaller heading inside a section.</summary>
public sealed record SubheadDef(string Text) : OptionDef;

/// <param name="Tip">Text for an info icon beside the heading.</param>
public sealed record SectionDef(string Heading, IReadOnlyList<OptionDef> Items, string? Tip = null);

/// <summary>
/// The Options pages that are lists of settings, modelled on Power BI Desktop's. Pages with
/// richer layouts (Data Load, Regional Settings, …) are written in OptionsWindow.axaml and may add sections here.
/// </summary>
public static class OptionsCatalog
{
    // Global keys, saved in Preferences.Options.
    public const string ShowQuerySettings = "pq.querySettings";
    public const string ShowFormulaBar = "pq.formulaBar";
    public const string AllowParameters = "pq.parameters";
    public const string Intellisense = "pq.intellisense";
    public const string ShowWhitespace = "pq.whitespace";
    public const string MonospacedPreview = "pq.monospace";
    public const string UnrestrictedMeasures = "dq.unrestrictedMeasures";
    public const string RHome = "r.home";
    public const string RHomeCustom = "r.homeCustom";
    public const string RIde = "r.ide";
    public const string PythonHome = "py.home";
    public const string PythonHomeCustom = "py.homeCustom";
    public const string PythonIde = "py.ide";
    public const string ApproveNativeQueries = "security.nativeQueries";
    public const string CertificateRevocation = "security.revocation";
    public const string WebPreviewWarning = "security.webPreview";
    public const string DataExtensions = "security.extensions";
    public const string DesktopBridge = "security.bridge";
    public const string CustomVisualWarning = "security.customVisuals";
    public const string MapVisuals = "security.maps";
    public const string DefaultBrowser = "security.browser";
    public const string GlobalPrivacy = "privacy.global";
    public const string Tracing = "diagnostics.tracing";
    public const string BypassGeocoding = "diagnostics.bypassGeocoding";
    public const string CrashDumps = "diagnostics.crashDumps";
    public const string QueryDiagnostics = "diagnostics.query";
    public const string AggregatedDiagnostics = "diagnostics.aggregated";
    public const string DetailedDiagnostics = "diagnostics.detailed";
    public const string PerformanceCounters = "diagnostics.performance";
    public const string PrivacyPartitions = "diagnostics.privacyPartitions";
    public const string PreviewDecompositionTree = "preview.decompositionTree";
    public const string PreviewKeyInfluencers = "preview.keyInfluencers";
    public const string AutoRecovery = "save.autoRecovery";
    public const string AutoRecoveryMinutes = "save.minutes";
    public const string KeepRecoveryOnClose = "save.keepOnClose";
    public const string RecoveryLocation = "save.recoveryFolder";
    public const string SaveToComputer = "save.toComputer";
    public const string DefaultSaveLocation = "save.defaultFolder";

    /// <summary>Set by the owner so action buttons can open folders; tests leave it null.</summary>
    public static Action<string>? OpenFolder { get; set; }

    public static string TracesFolder => Path.Combine(AppSettings.DefaultFolder, "Traces");
    public static string RecoveryFolder => Path.Combine(AppSettings.DefaultFolder, "AutoRecovery");
    public static string BackupFolder => Path.Combine(AppSettings.DefaultFolder, "Backup");

    public static string DocumentsFolder
    {
        get
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Directory.Exists(docs) ? docs : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }

    /// <summary>A folder the user chose, or <paramref name="fallback"/> when they haven't.</summary>
    public static string Folder(IReadOnlyDictionary<string, string>? store, string key, string fallback) =>
        store is not null && store.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

    /// <summary>Set by the Options window so Browse buttons can show a folder picker.</summary>
    public static Func<string, Task<string?>>? PickFolder { get; set; }

    const string NoEffect = "JuiceCI keeps these settings with your preferences, but has no feature they change yet.";
    const string NoEffectFile = "JuiceCI saves these settings with the report, but has no feature they change yet.";

    static void Open(string folder)
    {
        Directory.CreateDirectory(folder);
        OpenFolder?.Invoke(folder);
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<SectionDef>> Pages { get; } = new Dictionary<string, IReadOnlyList<SectionDef>>
    {
        ["pq"] =
        [
            new("Layout", [new CheckDef(ShowQuerySettings, "Display the Query Settings pane", true),
                new CheckDef(ShowFormulaBar, "Display the Formula Bar", true)]),
            new("Data Preview",
            [
                new CheckDef(MonospacedPreview, "Display preview contents using a monospaced font", false,
                    "Applies to the data preview in Table view."),
                new CheckDef(ShowWhitespace, "Show whitespace and newline characters", false,
                    "Shows spaces as ·, tabs as → and line breaks as ↵ in Table view's data preview."),
            ]),
            new("Parameters", [new CheckDef(AllowParameters, "Always allow parameterization in data source and transformation dialogs")]),
            new("Formula", [new CheckDef(Intellisense,
                "Enable M Intellisense in the formula bar, advanced editor, and custom column dialog. (Legacy Power Query editor)", true,
                "Suggests functions, columns and keywords as you type M formulas.")]),
        ],
        ["directquery"] =
        [
            new("DirectQuery options",
            [
                new NoteDef("JuiceCI always imports data into its own engine, so DirectQuery settings are kept but have no effect."),
                new CheckDef(UnrestrictedMeasures, "Allow unrestricted measures in DirectQuery mode"),
            ]),
        ],
        ["r"] = Scripting("R", RHome, RHomeCustom, RIde, DetectR, DetectRIdes),
        ["python"] = Scripting("Python", PythonHome, PythonHomeCustom, PythonIde, DetectPython, DetectPythonIdes),
        ["security"] =
        [
            new("Native Database Queries", [new CheckDef(ApproveNativeQueries, "Require user approval for new native database queries", true)]),
            new("Certificate Revocation",
            [
                new RadioDef(CertificateRevocation, ["Comprehensive check", "Basic check", "None"], 1,
                [
                    "Rejects a web source's HTTPS certificate if it's revoked or its revocation status can't be checked.",
                    "Rejects a web source's HTTPS certificate only if it's known to be revoked.",
                    "Doesn't check HTTPS certificates for revocation.",
                ]),
            ], "Checks whether the HTTPS certificates of web data sources have been revoked. " + NoEffect),
            new("Web Preview Warning Level",
                [new ListDef(WebPreviewWarning, null, () => ["Strict", "Moderate", "None"], "Moderate")],
                "How readily a warning is shown before previewing web pages from a web data source."),
            new("Data Extensions", [new RadioDef(DataExtensions,
            [
                "(Recommended) Only allow certified and other trusted third-party extensions to load",
                "(Not Recommended) Allow any extension to load without validation or warning",
            ])]),
            new("Desktop Bridge", [new CheckDef(DesktopBridge, "Enable external tool access to JuiceCI through secure local APIs", true)]),
            new("Custom visuals", [new CheckDef(CustomVisualWarning, "Show security warning when adding a custom visual to a report", true)]),
            new("Map and Filled Map visuals", [new CheckDef(MapVisuals, "Use Map and Filled Map visuals")]),
            new("Authentication Browser", [new CheckDef(DefaultBrowser, "Use my default web browser")]),
        ],
        ["privacy"] =
        [
            new("Privacy Levels",
            [
                new NoteDef(NoEffect),
                new RadioDef(GlobalPrivacy,
                [
                    "Always combine data according to your Privacy Level settings for each source",
                    "Combine data according to each file's Privacy Level settings",
                    "Always ignore Privacy Level settings",
                ], 1),
            ]),
        ],
        ["diagnostics"] =
        [
            new("Diagnostic Options",
            [
                new CheckDef(Tracing, "Enable tracing", false,
                    "Writes every query JuiceCI sends to its engine to a log in the traces folder, with the time it was sent."),
                new CheckDef(BypassGeocoding, "Bypass geocoding cache"),
            ], "Tracing records what JuiceCI does, to help find the cause of a problem."),
            new("Crash Dump Collection",
            [
                new ToggleDef(CrashDumps, "Crash dump collection for the JuiceCI process is currently enabled.",
                    "Crash dump collection for the JuiceCI process is currently not enabled.", "Enable Now", "Disable"),
                new ActionDef("Open crash dump/traces folder", _ => Open(TracesFolder), IsLink: true),
                new ActionDef("Clear Traces folder", _ => ClearTraces(), IsLink: true),
                new ActionDef("Collect diagnostic information", _ => Open(Path.GetDirectoryName(CollectDiagnostics())!)),
            ]),
            new("Version", [new NoteDef(VersionInfo())]),
            new("Query Diagnostics",
            [
                new RadioDef(QueryDiagnostics,
                    ["Enable in Report and Query Editor (may require running as admin)", "Enable in Query Editor (does not require running as admin)"]),
                new SubheadDef("Diagnostics Level"),
                new CheckDef(AggregatedDiagnostics, "Aggregated", true, "A summary of each query's evaluation: the time it took and the rows it returned."),
                new CheckDef(DetailedDiagnostics, "Detailed", true, "Every step of each query's evaluation, with its own timing."),
                new SubheadDef("Additional Diagnostics"),
                new CheckDef(PerformanceCounters, "Performance counters", true, "Memory and processor use while queries run."),
                new CheckDef(PrivacyPartitions, "Data privacy partitions", true, "How privacy levels split queries into separately evaluated parts."),
            ]),
        ],
        ["preview"] =
        [
            new("Preview features",
            [
                new NoteDef("The following features are available for you to try in this release. Preview features might change or be removed in future releases."),
                new CheckDef("preview.redshift", "Use new Amazon Redshift connector implementation"),
                new CheckDef("preview.sharepointViews", "Enable SharePoint list queries to respect view filters and sort order set on the SharePoint website"),
                new CheckDef("preview.dataflowGen2", "Enable DirectQuery for Dataflow Gen2 data sources", true),
                new CheckDef("preview.legacyDatabricks", "Use legacy Databricks connector implementation"),
                new CheckDef("preview.legacyBigQuery", "Use legacy Google BigQuery connector implementation"),
                new CheckDef("preview.legacySnowflake", "Use legacy Snowflake connector implementation"),
                new CheckDef("preview.qnaSpanish", "Spanish language support for Q&A"),
                new CheckDef("preview.qnaLive", "Q&A for live connected Analysis Services databases"),
                new CheckDef("preview.externalModels", "Connect to external semantic models shared with me"),
                new CheckDef("preview.metrics", "Metrics visual", true),
                new CheckDef("preview.onObject", "On-object interaction"),
                new CheckDef("preview.pdfLabel", "Set sensitivity label on exported PDF", true),
                new CheckDef("preview.oneDriveSave", "Save to OneDrive and SharePoint", true),
                new CheckDef("preview.oneDriveShare", "Share to OneDrive and SharePoint", true, Parent: "preview.oneDriveSave"),
                new CheckDef("preview.publishFolders", "Publish dialogs support folder selection"),
                new CheckDef("preview.pbipReload", "Detect and reload external PBIP changes.", true),
                new CheckDef("preview.newPowerQuery", "New Power Query experience"),
                new CheckDef("preview.daxTimeIntelligence", "Enhanced DAX Time Intelligence"),
                new CheckDef("preview.prepForAi", "Prep data for AI", true),
                new CheckDef("preview.oracleImport", "Enable using Bundled Oracle Managed ODP Provider for Import Mode", true),
                new CheckDef("preview.oracleDirectQuery", "Enable using Bundled Oracle Managed ODP Provider for DirectQuery Mode",
                    Parent: "preview.oracleImport"),
                new CheckDef("preview.fabricSql", "Connect to SQL databases in Fabric", true),
                new CheckDef("preview.warehouseSnapshot", "Warehouse snapshot", true),
                new CheckDef("preview.userContextColumns", "User-context-aware calculated columns"),
                new CheckDef("preview.exportQueries", "Export Queries from Power Query", true),
                new CheckDef("preview.gantt", "Gantt chart", true),
                new CheckDef("preview.firewallPartitions",
                    "Allow data privacy firewall partitions that reference other partitions to also access data sources", true),
                new CheckDef("preview.outlookMail", "Microsoft Outlook Mail connector"),
                new CheckDef(PreviewDecompositionTree, "Decomposition tree visual", true,
                    "Untick to hide it from the Visualizations pane and the ribbon."),
                new CheckDef(PreviewKeyInfluencers, "Key influencers visual", true,
                    "Untick to hide it from the Visualizations pane and the ribbon."),
            ]),
        ],
        ["save"] =
        [
            new("Auto recovery",
            [
                new CheckNumberDef(AutoRecovery, "Store auto recovery information every", AutoRecoveryMinutes, "minutes", true, 10, 1, 120),
                new CheckDef(KeepRecoveryOnClose, "Keep the last auto recovery version if I close without saving"),
                new FolderDef(RecoveryLocation, "Auto recovery file location:", () => RecoveryFolder),
                new ActionDef("Open auto recovery folder", s => Open(Folder(s, RecoveryLocation, RecoveryFolder)), IsLink: true),
            ]),
            new("Save",
            [
                new CheckDef(SaveToComputer, "Save to computer by default", false,
                    "JuiceCI always saves reports to this computer; this is kept for when it can save elsewhere."),
                new FolderDef(DefaultSaveLocation, "Default local file location:", () => DocumentsFolder),
            ]),
            new("Report backup",
            [
                new ActionDef("Open backup folder", _ => Open(BackupFolder), IsLink: true),
            ], "Before a save replaces a report, JuiceCI keeps the previous version here."),
        ],

        // ---- Current file ----

        ["fdata"] =
        [
            new("Type Detection",
            [
                new NoteDef("Used when Global › Data Load › Type Detection follows each file's setting."),
                new CheckDef(F.TypeDetection, "Detect column types and headers for unstructured sources", true),
            ]),
            new("Relationships",
            [
                new NoteDef(NoEffectFile),
                new CheckDef(F.ImportRelationships, "Import relationships from data sources on first load", true),
                new CheckDef(F.UpdateRelationships, "Update or delete relationships when refreshing data"),
                new CheckDef(F.AutodetectRelationships, "Autodetect new relationships after data is loaded", true),
            ]),
            new("Time intelligence", [new CheckDef(F.AutoDateTime, "Auto date/time", true)]),
            new("Background Data", [new CheckDef(F.BackgroundPreview, "Allow data preview to download in the background", true)]),
            new("Parallel loading of tables", [new RadioDef(F.ParallelLoading, ["Default", "One (disable parallel loading)"])]),
            new("Q&A", [new CheckDef(F.QnA, "Turn on Q&A to ask natural language questions about your data", true)]),
        ],
        ["fregional"] =
        [
            new("Locale for import",
            [
                new NoteDef("Sets the locale used to interpret numbers, dates and times in imported text files. " + NoEffectFile),
                new ListDef(F.ImportLocale, "Locale", () => OptionsViewModel.Cultures.Select(c => c.Label).ToList()),
            ]),
        ],
        ["fprivacy"] =
        [
            new("Privacy Levels",
            [
                new NoteDef(NoEffectFile),
                new RadioDef(F.PrivacyLevels,
                [
                    "Combine data according to your Privacy Level settings for each source",
                    "Ignore the Privacy levels and potentially improve performance",
                ]),
            ]),
        ],
        ["frecovery"] =
        [
            new("Auto recovery", [new CheckDef(F.DisableAutoRecovery, "Disable auto recovery for this file", false,
                "Unsaved changes to this report won't be copied to the auto recovery folder.")]),
        ],
        ["fpublished"] =
        [
            new("SAP Variables", [new CheckDef(F.SapVariables, "Allow report readers to change SAP variables for this report.", Locked: true,
                Tip: "Only available for reports with a DirectQuery connection to SAP, which JuiceCI doesn't make.")]),
            new("DirectQuery connections to this semantic model",
            [
                new NoteDef("This prevents users from creating DirectQuery connections to this semantic model in Power BI Desktop. "
                    + "If you change this setting, you'll need to republish your report to save it."),
                new CheckDef(F.DiscourageDirectQuery, "Discourage DirectQuery connections"),
            ]),
        ],
        ["fquery"] =
        [
            new("Reduce number of queries sent by",
            [
                new NoteDef(NoEffectFile),
                new CheckDef(F.DisableCrossHighlight, "Disabling cross highlighting/filtering by default"),
            ]),
            new("Slicers", [new RadioDef(F.SlicerApply,
                ["Instantly apply slicer changes", "Add an Apply button to each slicer to apply changes when you're ready"])]),
            new("Filters", [new RadioDef(F.FilterApply,
            [
                "Instantly apply basic filter changes",
                "Add an Apply button to each basic filter to apply changes when you're ready",
                "Add a single Apply button to the filter pane to apply changes at once",
            ])]),
        ],
        ["freport"] =
        [
            new("Persistent filters", [new CheckDef(F.PersistentFiltersDisabled, "Don't allow end user to save filters on this file in the Power BI service")]),
            new("Visual options",
            [
                new CheckDef(F.HideHeaderInReading, "Hide the visual header in reading view"),
                new CheckDef(F.ModernHeader, "Use the modern visual header with updated styling options", true),
                new CheckDef(F.CrossFilterDefault, "Change default visual interaction from cross highlighting to cross filtering"),
            ]),
            new("Export data", [new RadioDef(F.ExportData,
            [
                "Allow end users to export data with current layout and summarized data from the Power BI service or Power BI Report Server",
                "Allow end users to export data with current layout, summarized data and underlying data from the service or Report Server",
                "Don't allow end users to export any data from the service or Report Server",
            ])]),
            new("Filtering experience",
            [
                new CheckDef(F.ChangeFilterTypes, "Allow users to change filter types", true),
                new CheckDef(F.FilterPaneSearch, "Enable search for the filter pane", true, "Shows a search box at the top of the Filters pane."),
            ]),
            new("Cross-report drillthrough", [new CheckDef(F.CrossReportDrillthrough, "Allow visuals in this report to use drillthrough targets from other reports")]),
            new("Personalize visuals", [new CheckDef(F.PersonalizeVisuals, "Allow report readers to personalize visuals to suit their needs")]),
            new("Develop a visual", [new CheckDef(F.OverrideVisualVersion,
                "Override the available AppSource visual's version for this session, so you can upload and test a visual file")]),
            new("Tooltips auto-scale (preview)", [new CheckDef(F.TooltipAutoScale, "Tooltip size is affected by canvas size")]),
            new("Default summarizations", [new CheckDef(F.DefaultSummarization, "For aggregated fields, always show the default summarization type")]),
            new("Query-limit simulations",
            [
                new NoteDef("Visual queries have different timeout and memory limits related to where your model is hosted. These limits may "
                    + "cause queries to fail for users who open a report online, but not in JuiceCI. To test the query limits for online "
                    + "users, select a capacity to simulate."),
                new ListDef(F.QueryLimit, null, () => F.QueryLimits, F.QueryLimits[0]),
            ]),
            new("Field parameters options", [new CheckDef(F.PersistHierarchyLevel, "Persist hierarchy level", true)]),
        ],
    };

    static IReadOnlyList<SectionDef> Scripting(string language, string home, string custom, string ide,
        Func<IReadOnlyList<string>> homes, Func<IReadOnlyList<string>> ides) =>
    [
        new($"{language} script options",
        [
            new NoteDef($"{language} visuals aren't drawn in JuiceCI yet; the installation you pick is saved for when they are."),
            new ListDef(home, $"Detected {language} home directories", homes),
            new TextDef(custom, "Other: set a home directory"),
            new ListDef(ide, $"Detected {language} IDEs", ides),
        ]),
    ];

    static readonly Dictionary<string, string> Defaults = Pages.Values.SelectMany(p => p).SelectMany(s => s.Items)
        .Select(d => d switch
        {
            CheckDef c => (c.Key, c.Default ? "true" : "false"),
            ToggleDef t => (t.Key, "false"),
            CheckNumberDef cn => (cn.Key, cn.Default ? "true" : "false"),
            RadioDef r => (r.Key, r.Default.ToString(CultureInfo.InvariantCulture)),
            NumberDef n => (n.Key, n.Default.ToString(CultureInfo.InvariantCulture)),
            TextDef t => (t.Key, t.Default),
            _ => (null!, null!),
        })
        .Concat(Pages.Values.SelectMany(p => p).SelectMany(s => s.Items).OfType<CheckNumberDef>()
            .Select(cn => (cn.NumberKey, cn.DefaultNumber.ToString(CultureInfo.InvariantCulture))))
        .Where(p => p.Item1 is not null)
        .ToDictionary(p => p.Item1, p => p.Item2);

    public static string? Get(IReadOnlyDictionary<string, string>? store, string key) =>
        store is not null && store.TryGetValue(key, out var v) ? v : Defaults.GetValueOrDefault(key);

    public static bool Flag(IReadOnlyDictionary<string, string>? store, string key) => Get(store, key) == "true";

    public static int Number(IReadOnlyDictionary<string, string>? store, string key) =>
        int.TryParse(Get(store, key), CultureInfo.InvariantCulture, out var n) ? n : 0;

    public static string AppVersion =>
        typeof(OptionsCatalog).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>Like Power BI's "2.159.883.0 64-bit (October 2026)": the build date is when the app was built.</summary>
    static string VersionInfo()
    {
        var assembly = typeof(OptionsCatalog).Assembly;
        var built = File.Exists(assembly.Location) ? File.GetLastWriteTime(assembly.Location) : DateTime.Now;
        var version = assembly.GetName().Version?.ToString() ?? "1.0.0.0";
        return $"{version} {(Environment.Is64BitProcess ? "64" : "32")}-bit ({built.ToString("MMMM yyyy", CultureInfo.InvariantCulture)})";
    }

    static string SystemInfo() =>
        $"JuiceCI {VersionInfo()}{Environment.NewLine}.NET {Environment.Version}{Environment.NewLine}"
        + $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}){Environment.NewLine}"
        + $"{Environment.ProcessorCount} logical processors{Environment.NewLine}";

    /// <summary>Deletes trace and crash logs; ones still being written stay.</summary>
    public static void ClearTraces(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    static void ClearTraces() => ClearTraces(TracesFolder);

    /// <summary>Zips the traces, the settings and a description of this machine, for attaching to a bug report.</summary>
    public static string CollectDiagnostics(string settingsFolder)
    {
        var folder = Path.Combine(settingsFolder, "Diagnostics");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"juiceci-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("system.txt").Open()))
            writer.Write(SystemInfo());
        var settings = Path.Combine(settingsFolder, "settings.json");
        if (File.Exists(settings)) zip.CreateEntryFromFile(settings, "settings.json");
        var traces = Path.Combine(settingsFolder, "Traces");
        if (Directory.Exists(traces))
            foreach (var file in Directory.EnumerateFiles(traces))
            {
                try
                {
                    zip.CreateEntryFromFile(file, "Traces/" + Path.GetFileName(file));
                }
                catch (IOException)
                {
                    // Being written right now; the rest still help.
                }
            }
        return path;
    }

    static string CollectDiagnostics() => CollectDiagnostics(AppSettings.DefaultFolder);

    // ---- Script installations ----

    const string DefaultIde = "Default program for the file type";

    /// <summary>Directories on PATH (and common install locations) holding one of <paramref name="names"/>.</summary>
    static IReadOnlyList<string> FindHomes(string[] names, IEnumerable<string> extra)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(extra);
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        return dirs.Where(d => names.Any(n => File.Exists(Path.Combine(d, n + exe))))
            .Select(d => Path.GetFileName(d) is "bin" or "Scripts" ? Path.GetDirectoryName(d) ?? d : d)
            .Distinct(StringComparer.Ordinal).ToList();
    }

    static IReadOnlyList<string> FindPrograms(params (string Exe, string Name)[] programs)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        return programs.Where(p => dirs.Any(d => File.Exists(Path.Combine(d, p.Exe + exe)))).Select(p => p.Name)
            .Prepend(DefaultIde).ToList();
    }

    static string Home(params string[] parts) =>
        Path.Combine([Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), .. parts]);

    static IEnumerable<string> Subdirs(string folder, string suffix) =>
        Directory.Exists(folder) ? Directory.GetDirectories(folder).Select(d => Path.Combine(d, suffix)) : [];

    static IReadOnlyList<string> DetectPython() => FindHomes(["python3", "python"],
    [
        Home("miniconda3", "bin"), Home("anaconda3", "bin"), Home(".local", "bin"),
        .. Subdirs(Home(".pyenv", "versions"), "bin"), .. Subdirs(Home("miniconda3", "envs"), "bin"),
    ]);

    static IReadOnlyList<string> DetectPythonIdes() => FindPrograms(
        ("code", "Visual Studio Code"), ("pycharm", "PyCharm"), ("spyder", "Spyder"), ("jupyter-lab", "JupyterLab"), ("idle3", "IDLE"));

    static IReadOnlyList<string> DetectR() => FindHomes(["R", "Rscript"],
        ["/usr/lib/R/bin", "/usr/lib64/R/bin", .. Subdirs("/opt/R", "bin")]);

    static IReadOnlyList<string> DetectRIdes() => FindPrograms(
        ("rstudio", "RStudio"), ("positron", "Positron"), ("code", "Visual Studio Code"));
}

// ---- What the Options window binds to for catalog pages ----

public sealed record OptionSectionViewModel(string Heading, IReadOnlyList<OptionItemViewModel> Items, string? Tip)
{
    public bool HasTip => Tip is not null;
}

public abstract class OptionItemViewModel : ObservableObject;

public sealed class CheckItemViewModel(Dictionary<string, string> store, CheckDef def) : OptionItemViewModel
{
    public string Key => def.Key;
    public string Label => def.Label;
    public string? Tip => def.Tip;
    public bool HasTip => def.Tip is not null;
    public Thickness Indent => def.Parent is null ? default : new Thickness(24, 0, 0, 0);
    public bool IsEnabled => !def.Locked && (def.Parent is null || OptionsCatalog.Flag(store, def.Parent));

    /// <summary>Lets an indented checkbox follow its parent's state.</summary>
    internal void Follow(CheckItemViewModel parent) =>
        parent.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsChecked)) OnPropertyChanged(nameof(IsEnabled));
        };

    public bool IsChecked
    {
        get => OptionsCatalog.Flag(store, def.Key);
        set
        {
            store[def.Key] = value ? "true" : "false";
            OnPropertyChanged();
        }
    }
}

public sealed class RadioItemViewModel(Dictionary<string, string> store, string key, int index, string label, string group, string? tip = null)
    : OptionItemViewModel
{
    public string Label => label;
    public string? Tip => tip;
    public bool HasTip => tip is not null;
    public string Group => group;

    public bool IsChecked
    {
        get => OptionsCatalog.Number(store, key) == index;
        set
        {
            if (!value) return;
            store[key] = index.ToString(CultureInfo.InvariantCulture);
            OnPropertyChanged();
        }
    }
}

public sealed class ListItemViewModel : OptionItemViewModel
{
    readonly Dictionary<string, string> _store;
    readonly string _key;

    public ListItemViewModel(Dictionary<string, string> store, ListDef def)
    {
        (_store, _key, Label, _default) = (store, def.Key, def.Label, def.Default);
        var items = def.Items().ToList();
        // A saved choice that's no longer found (an uninstalled Python, say) still shows.
        if (store.TryGetValue(def.Key, out var saved) && !items.Contains(saved)) items.Insert(0, saved);
        if (items.Count == 0) items.Add("None detected");
        Items = items;
    }

    readonly string? _default;

    public string? Label { get; }
    public bool HasLabel => Label is not null;
    public IReadOnlyList<string> Items { get; }

    public string? Selected
    {
        get => _store.TryGetValue(_key, out var v) ? v : _default is not null && Items.Contains(_default) ? _default : Items[0];
        set
        {
            if (value is null) return;
            _store[_key] = value;
            OnPropertyChanged();
        }
    }
}

public sealed class NumberItemViewModel(Dictionary<string, string> store, NumberDef def) : OptionItemViewModel
{
    public string Label => def.Label;
    public decimal Minimum => def.Min;
    public decimal Maximum => def.Max;

    public decimal? Value
    {
        get => OptionsCatalog.Number(store, def.Key);
        set
        {
            store[def.Key] = ((int)Math.Clamp(value ?? def.Default, def.Min, def.Max)).ToString(CultureInfo.InvariantCulture);
            OnPropertyChanged();
        }
    }
}

public sealed class TextItemViewModel(Dictionary<string, string> store, TextDef def) : OptionItemViewModel
{
    public string Label => def.Label;
    public bool Multiline => def.Multiline;
    public double Height => def.Multiline ? 96 : double.NaN;

    public string Value
    {
        get => OptionsCatalog.Get(store, def.Key) ?? "";
        set
        {
            store[def.Key] = value;
            OnPropertyChanged();
        }
    }
}

public sealed class NoteItemViewModel(string text) : OptionItemViewModel
{
    public string Text => text;
}

public sealed class ActionItemViewModel(Dictionary<string, string> store, ActionDef def) : OptionItemViewModel
{
    public string Label => def.Label;
    public bool IsLink => def.IsLink;
    public bool IsButton => !def.IsLink;
    public IRelayCommand Command { get; } = new RelayCommand(() => def.Run(store));
}

public sealed class CheckNumberItemViewModel(Dictionary<string, string> store, CheckNumberDef def) : OptionItemViewModel
{
    public string Before => def.Before;
    public string After => def.After;
    public decimal Minimum => def.Min;
    public decimal Maximum => def.Max;

    public bool IsChecked
    {
        get => OptionsCatalog.Flag(store, def.Key);
        set
        {
            store[def.Key] = value ? "true" : "false";
            OnPropertyChanged();
        }
    }

    public decimal? Value
    {
        get => OptionsCatalog.Number(store, def.NumberKey) is var n && n > 0 ? n : def.DefaultNumber;
        set
        {
            store[def.NumberKey] = ((int)Math.Clamp(value ?? def.DefaultNumber, def.Min, def.Max)).ToString(CultureInfo.InvariantCulture);
            OnPropertyChanged();
        }
    }
}

public sealed class FolderItemViewModel : OptionItemViewModel
{
    readonly Dictionary<string, string> _store;
    readonly FolderDef _def;

    public FolderItemViewModel(Dictionary<string, string> store, FolderDef def)
    {
        (_store, _def) = (store, def);
        BrowseCommand = new AsyncRelayCommand(async () =>
        {
            if (OptionsCatalog.PickFolder is { } pick && await pick(Path) is { } chosen) Path = chosen;
        });
    }

    public string Label => _def.Label;

    public string Path
    {
        get => OptionsCatalog.Folder(_store, _def.Key, _def.Default());
        set
        {
            if (value == Path) return;
            _store[_def.Key] = value;
            OnPropertyChanged();
        }
    }

    public IAsyncRelayCommand BrowseCommand { get; }
}

public sealed class ToggleItemViewModel : OptionItemViewModel
{
    readonly Dictionary<string, string> _store;
    readonly ToggleDef _def;

    public ToggleItemViewModel(Dictionary<string, string> store, ToggleDef def)
    {
        (_store, _def) = (store, def);
        ToggleCommand = new RelayCommand(() =>
        {
            _store[_def.Key] = IsOn ? "false" : "true";
            OnPropertyChanged(nameof(Text));
            OnPropertyChanged(nameof(ButtonLabel));
        });
    }

    bool IsOn => OptionsCatalog.Flag(_store, _def.Key);
    public string Text => IsOn ? _def.OnText : _def.OffText;
    public string ButtonLabel => IsOn ? _def.DisableLabel : _def.EnableLabel;
    public IRelayCommand ToggleCommand { get; }
}

public sealed class SubheadItemViewModel(string text) : OptionItemViewModel
{
    public string Text => text;
}
