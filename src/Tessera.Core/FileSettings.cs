using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Tessera.Core;

/// <summary>
/// Keys for <see cref="ReportDocument.Settings"/>, the Options dialog's Current file pages.
/// Checkboxes store "true"/"false" and radio groups the chosen option's index.
/// </summary>
public static class FileSettings
{
    public const string TypeDetection = "data.typeDetection";
    public const string ImportRelationships = "data.importRelationships";
    public const string UpdateRelationships = "data.updateRelationships";
    public const string AutodetectRelationships = "data.autodetectRelationships";
    public const string AutoDateTime = "data.autoDateTime";
    public const string BackgroundPreview = "data.backgroundPreview";
    public const string ParallelLoading = "data.parallelLoading";
    public const string QnA = "data.qna";
    public const string ImportLocale = "regional.locale";
    public const string PrivacyLevels = "privacy.levels";
    public const string DisableAutoRecovery = "recovery.disabled";
    public const string SapVariables = "published.sapVariables";
    public const string DiscourageDirectQuery = "published.discourageDirectQuery";
    public const string DisableCrossHighlight = "query.disableCrossHighlight";
    public const string SlicerApply = "query.slicerApply";
    public const string FilterApply = "query.filterApply";
    public const string PersistentFiltersDisabled = "report.persistentFiltersDisabled";
    public const string HideHeaderInReading = "report.hideHeaderInReading";
    public const string ModernHeader = "report.modernHeader";
    public const string CrossFilterDefault = "report.crossFilterDefault";
    public const string ExportData = "report.exportData";
    public const string ChangeFilterTypes = "report.changeFilterTypes";
    public const string FilterPaneSearch = "report.filterPaneSearch";
    public const string HideFilterPaneForReaders = "report.hideFilterPane";
    public const string CrossReportDrillthrough = "report.crossReportDrillthrough";
    public const string PersonalizeVisuals = "report.personalizeVisuals";
    public const string OverrideVisualVersion = "report.overrideVisualVersion";
    public const string DefaultSummarization = "report.defaultSummarization";
    public const string TooltipAutoScale = "report.tooltipAutoScale";
    public const string QueryLimit = "report.queryLimit";
    public const string PersistHierarchyLevel = "report.persistHierarchyLevel";

    /// <summary>The Query-limit simulations drop-down; <see cref="QueryLimit"/> stores one of these.</summary>
    public static readonly IReadOnlyList<string> QueryLimits = ["Default", "No query limits", "Custom limit"];

    /// <summary>The report settings a .pbix saved, in either report format; null when it saved none.</summary>
    public static Dictionary<string, string>? FromPbix(ZipArchive zip)
    {
        var result = new Dictionary<string, string>();
        if (zip.GetEntry("Report/definition/report.json") is { } report)
        {
            using var doc = JsonDocument.Parse(ReadText(report));
            ReadReport(doc.RootElement, result);
        }
        else if (zip.GetEntry("Report/Layout") is { } layout)
        {
            using var doc = JsonDocument.Parse(ReadText(layout));
            if (doc.RootElement.TryGetProperty("config", out var c) && c.ValueKind == JsonValueKind.String)
            {
                using var config = JsonDocument.Parse(c.GetString()!);
                ReadReport(config.RootElement, result);
            }
        }
        if (zip.GetEntry("Settings") is { } settings)
        {
            try
            {
                using var doc = JsonDocument.Parse(ReadText(settings));
                if (doc.RootElement.TryGetProperty("QueriesSettings", out var q))
                {
                    Flag(q, "TypeDetectionEnabled", TypeDetection, result);
                    Flag(q, "RelationshipImportEnabled", ImportRelationships, result);
                }
            }
            catch (JsonException)
            {
                // Older files keep other formats here; their report settings still count.
            }
        }
        return result.Count == 0 ? null : result;
    }

    static void ReadReport(JsonElement root, Dictionary<string, string> result)
    {
        if (root.TryGetProperty("settings", out var s) && s.ValueKind == JsonValueKind.Object)
        {
            Flag(s, "isPersistentUserStateDisabled", PersistentFiltersDisabled, result);
            Flag(s, "hideVisualContainerHeader", HideHeaderInReading, result);
            Flag(s, "useStylableVisualContainerHeader", ModernHeader, result);
            Flag(s, "defaultFilterActionIsDataFilter", CrossFilterDefault, result);
            Flag(s, "allowChangeFilterTypes", ChangeFilterTypes, result);
            Flag(s, "useCrossReportDrillthrough", CrossReportDrillthrough, result);
            if (s.TryGetProperty("queryLimitOption", out var limit) && limit.ValueKind == JsonValueKind.String)
            {
                string? choice = limit.GetString() switch
                {
                    "None" => QueryLimits[1], "Custom" => QueryLimits[2], "Default" or "Auto" => QueryLimits[0], _ => null,
                };
                if (choice is not null) result[QueryLimit] = choice;
            }
            if (s.TryGetProperty("exportDataMode", out var mode) && mode.ValueKind == JsonValueKind.String)
            {
                int? index = mode.GetString() switch
                {
                    "AllowSummarized" => 0, "AllowSummarizedAndUnderlying" => 1, "None" => 2, _ => null,
                };
                if (index is { } i) result[ExportData] = i.ToString();
            }
        }
        if (root.TryGetProperty("slowDataSourceSettings", out var slow) && slow.ValueKind == JsonValueKind.Object)
        {
            Flag(slow, "isCrossHighlightingDisabled", DisableCrossHighlight, result);
            if (Bool(slow, "isSlicerSelectionsButtonEnabled") is { } slicer) result[SlicerApply] = slicer ? "1" : "0";
            if (Bool(slow, "isApplyAllButtonEnabled") == true) result[FilterApply] = "2";
            else if (Bool(slow, "isFilterSelectionsButtonEnabled") is { } filter) result[FilterApply] = filter ? "1" : "0";
        }
    }

    static bool? Bool(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    static void Flag(JsonElement obj, string name, string key, Dictionary<string, string> result)
    {
        if (Bool(obj, name) is { } b) result[key] = b ? "true" : "false";
    }

    /// <summary>Power BI writes some parts as UTF-16 without a byte order mark.</summary>
    static string ReadText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[1] == 0) return Encoding.Unicode.GetString(bytes);
        return Encoding.UTF8.GetString(bytes).TrimStart('﻿');
    }
}
