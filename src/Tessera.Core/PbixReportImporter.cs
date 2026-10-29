using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Tessera.Core;

/// <summary>
/// Reads the report pages of a Power BI Desktop file, in either the enhanced (PBIR) format,
/// one JSON file per page and visual under <c>Report/definition</c>, or the older single
/// <c>Report/Layout</c> file. Charts, cards and tables become live JuiceCI visuals; text boxes,
/// shapes and images keep the page's look; anything else becomes a labelled placeholder.
/// </summary>
public static class PbixReportImporter
{
    /// <summary>Pages wider than this are scaled down so JuiceCI's fonts keep their proportions.</summary>
    const double TargetWidth = 1280;

    /// <summary>Returns null when the file has no report pages.</summary>
    /// <param name="tables">The engine's tables after importing the file's model, used to type each field.</param>
    public static ReportDocument? Read(string path, IReadOnlyList<TableInfo> tables)
    {
        using var zip = ZipFile.OpenRead(path);
        var reader = new LayoutReader(zip, tables);
        var pages = zip.GetEntry("Report/Layout") is { } layout ? reader.ReadLegacy(layout) : reader.ReadEnhanced();
        return pages.Count == 0 ? null
            : new ReportDocument
            {
                Pages = pages, Settings = FileSettings.FromPbix(zip), LandingPage = reader.LandingPage, Filters = reader.ReportFilters,
                Theme = reader.ReportTheme,
            };
    }

    /// <summary>Friendly names for Power BI visuals JuiceCI doesn't draw.</summary>
    static string FriendlyName(string visualType) => visualType switch
    {
        "slicer" or "advancedSlicerVisual" or "listSlicer" or "textSlicer" => "Slicer",
        "map" or "filledMap" or "azureMap" or "shapeMap" => "Map",
        "scatterChart" => "Scatter chart",
        "treemap" => "Treemap",
        "waterfallChart" => "Waterfall chart",
        "funnel" => "Funnel",
        "gauge" => "Gauge",
        "kpi" => "KPI",
        "ribbonChart" => "Ribbon chart",
        "qnaVisual" => "Q&A",
        "scriptVisual" or "pythonVisual" => "R or Python visual",
        "aiNarratives" => "Smart narrative",
        "actionButton" => "Button",
        "bookmarkNavigator" => "Bookmark navigator",
        _ => visualType,
    };

    static VisualType? DataVisualType(string visualType) => visualType switch
    {
        "barChart" or "clusteredBarChart" or "hundredPercentStackedBarChart" => VisualType.BarChart,
        "columnChart" or "clusteredColumnChart" or "hundredPercentStackedColumnChart"
            or "lineClusteredColumnComboChart" or "lineStackedColumnComboChart" => VisualType.ColumnChart,
        "lineChart" => VisualType.LineChart,
        "areaChart" or "stackedAreaChart" or "hundredPercentStackedAreaChart" => VisualType.AreaChart,
        "pieChart" => VisualType.PieChart,
        "donutChart" => VisualType.DonutChart,
        "card" or "cardVisual" or "multiRowCard" => VisualType.Card,
        "tableEx" or "pivotTable" or "table" or "matrix" => VisualType.Table,
        "decompositionTreeVisual" => VisualType.DecompositionTree,
        "keyDriversVisual" => VisualType.KeyInfluencers,
        _ => null,
    };

    /// <summary>A visual container in either format, before it's turned into a <see cref="VisualSpec"/>.</summary>
    sealed record RawVisual(
        string Type, double X, double Y, double Z, double Width, double Height,
        List<Projection> Projections,
        JsonElement Objects, JsonElement ContainerObjects,
        IReadOnlyDictionary<string, string> Sources, JsonElement Filters = default, MobilePosition? Mobile = null);

    /// <param name="Label">The name the report shows for the field, when renamed for the visual.</param>
    sealed record Projection(string Role, JsonElement Field, string? Label);

    sealed class LayoutReader(ZipArchive zip, IReadOnlyList<TableInfo> tables)
    {
        readonly Theme _theme = Theme.Load(zip);

        /// <summary>The landing page's display name, when the report names one.</summary>
        public string? LandingPage { get; private set; }

        /// <summary>Filters on all pages.</summary>
        public List<FieldFilter> ReportFilters { get; private set; } = [];

        /// <summary>The report's theme colours, so new visuals match the imported ones.</summary>
        public ReportTheme ReportTheme => _theme.ToReportTheme();

        // ---------------------------------------------------------- Enhanced (PBIR) format

        public List<ReportPage> ReadEnhanced()
        {
            const string root = "Report/definition/pages/";
            var folders = zip.Entries
                .Where(e => e.FullName.StartsWith(root) && e.FullName.EndsWith("/page.json"))
                .Select(e => e.FullName[root.Length..^"/page.json".Length])
                .Where(f => !f.Contains('/'))
                .ToList();
            if (Json(root + "pages.json") is { } order && order.RootElement.TryGetProperty("pageOrder", out var list))
            {
                var ordered = list.EnumerateArray().Select(p => p.GetString()!).Where(folders.Contains).ToList();
                folders = [.. ordered, .. folders.Except(ordered)];
            }
            var landing = Json(root + "pages.json") is { } meta ? Str(meta.RootElement, "landingPageName") : null;
            if (Json("Report/definition/report.json") is { } report && report.RootElement.TryGetProperty("filterConfig", out var rf))
                ReportFilters = ReadFilters(Child(rf, "filters"));

            var pages = new List<ReportPage>();
            foreach (var folder in folders)
            {
                using var page = Json($"{root}{folder}/page.json")!;
                var p = page.RootElement;
                var visuals = new List<RawVisual>();
                var prefix = $"{root}{folder}/visuals/";
                foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith(prefix) && e.FullName.EndsWith("/visual.json")))
                {
                    using var doc = Json(entry.FullName)!;
                    var v = doc.RootElement;
                    if (Bool(v, "isHidden") || !v.TryGetProperty("visual", out var visual)) continue;   // groups and hidden visuals
                    var projections = new List<Projection>();
                    if (visual.TryGetProperty("query", out var query) && query.TryGetProperty("queryState", out var state))
                        foreach (var role in state.EnumerateObject())
                            if (role.Value.TryGetProperty("projections", out var items))
                                foreach (var item in items.EnumerateArray())
                                    if (item.TryGetProperty("field", out var field))
                                        projections.Add(new Projection(role.Name, field.Clone(), Str(item, "displayName")));
                    var pos = v.GetProperty("position");
                    MobilePosition? mobile = null;
                    if (Json(entry.FullName[..^"visual.json".Length] + "mobile.json") is { } m && m.RootElement.TryGetProperty("position", out var mp))
                        mobile = new MobilePosition(Num(mp, "x"), Num(mp, "y"), Num(mp, "width"), Num(mp, "height"));
                    visuals.Add(new RawVisual(
                        visual.GetProperty("visualType").GetString() ?? "",
                        Num(pos, "x"), Num(pos, "y"), Num(pos, "z"), Num(pos, "width"), Num(pos, "height"),
                        projections,
                        Child(visual, "objects"), Child(visual, "visualContainerObjects"),
                        new Dictionary<string, string>(),
                        v.TryGetProperty("filterConfig", out var vf) ? Child(vf, "filters") : default, mobile));
                }
                var built = BuildPage(Str(p, "displayName") ?? folder, Num(p, "width", 1280), Num(p, "height", 720), visuals);
                if (folder == landing) LandingPage = built.Name;
                // A page colour from the theme stays with the theme, so changing themes changes it too.
                built.Background = _theme.Color(Prop(Child(p, "objects"), "background", "color"));
                if (p.TryGetProperty("filterConfig", out var pf)) built.Filters = ReadFilters(Child(pf, "filters"));
                pages.Add(built);
            }
            return pages;
        }

        // ---------------------------------------------------------- Legacy Report/Layout

        public List<ReportPage> ReadLegacy(ZipArchiveEntry entry)
        {
            string text;
            using (var stream = entry.Open())
            using (var reader = new StreamReader(stream, Encoding.Unicode, detectEncodingFromByteOrderMarks: true))
                text = reader.ReadToEnd();
            using var layout = JsonDocument.Parse(text);
            if (!layout.RootElement.TryGetProperty("sections", out var sections)) return [];
            ReportFilters = ReadFilters(ParseEmbedded(layout.RootElement, "filters"));

            var pages = new List<(double Ordinal, ReportPage Page)>();
            foreach (var section in sections.EnumerateArray())
            {
                var visuals = new List<RawVisual>();
                if (section.TryGetProperty("visualContainers", out var containers))
                {
                    foreach (var c in containers.EnumerateArray())
                    {
                        if (Str(c, "config") is not { } configText) continue;
                        using var config = JsonDocument.Parse(configText);
                        if (!config.RootElement.TryGetProperty("singleVisual", out var visual)) continue;
                        if (visual.TryGetProperty("display", out var display) && Str(display, "mode") == "hidden") continue;

                        // Projections name their fields by queryRef; the prototype query holds the fields themselves.
                        var sources = new Dictionary<string, string>();
                        var selects = new Dictionary<string, JsonElement>();
                        if (visual.TryGetProperty("prototypeQuery", out var proto))
                        {
                            if (proto.TryGetProperty("From", out var from))
                                foreach (var f in from.EnumerateArray())
                                    if (Str(f, "Name") is { } alias && Str(f, "Entity") is { } entity) sources[alias] = entity;
                            if (proto.TryGetProperty("Select", out var select))
                                foreach (var s in select.EnumerateArray())
                                    if (Str(s, "Name") is { } name) selects.TryAdd(name, s.Clone());
                        }
                        var projections = new List<Projection>();
                        visual.TryGetProperty("columnProperties", out var columnProperties);
                        if (visual.TryGetProperty("projections", out var roles))
                            foreach (var role in roles.EnumerateObject())
                                foreach (var item in role.Value.EnumerateArray())
                                    if (Str(item, "queryRef") is { } queryRef && selects.TryGetValue(queryRef, out var field))
                                        projections.Add(new Projection(role.Name, field,
                                            columnProperties.ValueKind == JsonValueKind.Object && columnProperties.TryGetProperty(queryRef, out var cp)
                                                ? Str(cp, "displayName") : null));

                        visuals.Add(new RawVisual(
                            Str(visual, "visualType") ?? "",
                            Num(c, "x"), Num(c, "y"), Num(c, "z"), Num(c, "width"), Num(c, "height"),
                            projections, Child(visual, "objects"), Child(visual, "vcObjects"), sources,
                            ParseEmbedded(c, "filters")));
                    }
                }
                var page = BuildPage(Str(section, "displayName") ?? "Page", Num(section, "width", 1280), Num(section, "height", 720), visuals);
                page.Filters = ReadFilters(ParseEmbedded(section, "filters"));
                page.Background = _theme.Color(Prop(ParseEmbedded(section, "config") is { ValueKind: JsonValueKind.Object } cfg
                    ? Child(cfg, "objects") : default, "background", "color"));
                pages.Add((Num(section, "ordinal", pages.Count), page));
            }
            return pages.OrderBy(p => p.Ordinal).Select(p => p.Page).ToList();
        }

        // ---------------------------------------------------------- Shared

        ReportPage BuildPage(string name, double width, double height, List<RawVisual> visuals)
        {
            var scale = width > TargetWidth ? TargetWidth / width : 1;
            var page = new ReportPage { Name = name, Width = Math.Round(width * scale), Height = Math.Round(height * scale) };
            foreach (var raw in visuals.OrderBy(v => v.Z))
            {
                var spec = BuildVisual(raw, scale);
                (spec.X, spec.Y) = (Math.Round(raw.X * scale), Math.Round(raw.Y * scale));
                (spec.Width, spec.Height) = (Math.Max(8, Math.Round(raw.Width * scale)), Math.Max(8, Math.Round(raw.Height * scale)));
                spec.Mobile = raw.Mobile;
                page.Visuals.Add(spec);
            }
            return page;
        }

        VisualSpec BuildVisual(RawVisual raw, double scale)
        {
            var style = new VisualStyle();
            var spec = new VisualSpec { Style = style, Filters = ReadFilters(raw.Filters) };
            if (Prop(raw.ContainerObjects, "title", "show") is { } show && Literal(show) == "false") style.HideTitle = true;
            if (Prop(raw.ContainerObjects, "background", "show") is not { } bgShow || Literal(bgShow) != "false")
                style.Background = _theme.Color(Prop(raw.ContainerObjects, "background", "color"));

            if (DataVisualType(raw.Type) is { } type)
            {
                spec.Type = type;
                if (Prop(raw.ContainerObjects, "title", "text") is { } title && Literal(title) is { Length: > 0 } t) spec.Title = t;
                style.TitleColor = _theme.Color(Prop(raw.ContainerObjects, "title", "fontColor"));
                style.Accent = _theme.Color(Prop(raw.Objects, "dataPoint", "fill"));
                style.HideValueAxis = Literal(Prop(raw.ContainerObjects, "stylePreset", "name")) == "Data labels"
                    || Literal(Prop(raw.Objects, "valueAxis", "show")) == "false";
                AddFields(spec, raw);
                if (type == VisualType.DecompositionTree)
                {
                    style.Accent = _theme.Color(Prop(raw.Objects, "dataBars", "positiveBarColor")) ?? style.Accent;
                    spec.Drill = ReadDrillPath(spec, raw);
                }
                else if (type == VisualType.KeyInfluencers)
                {
                    style.Accent = _theme.Color(Prop(raw.Objects, "keyInfluencersVisual", "primaryColor")) ?? style.Accent;
                    if (Literal(Prop(raw.Objects, "keyDrivers", "targetValue")) is { } target)
                        spec.TargetValue = target.Length >= 2 && target[0] == '\'' && target[^1] == '\'' ? target[1..^1] : target;
                }
                return spec;
            }

            switch (raw.Type)
            {
                case "textbox":
                    spec.Type = VisualType.Text;
                    ReadText(raw.Objects, style, scale);
                    // Text boxes are see-through unless a background is switched on.
                    if (Prop(raw.ContainerObjects, "background", "show") is not { } on || Literal(on) != "true") style.Background = null;
                    break;
                case "shape" or "basicShape":
                    spec.Type = VisualType.Shape;
                    if (Prop(raw.Objects, "fill", "show") is not { } fillShow || Literal(fillShow) != "false")
                        style.Background = _theme.Color(Prop(raw.Objects, "fill", "fillColor")) ?? style.Background;
                    break;
                case "image":
                    spec.Type = VisualType.Image;
                    style.Image = ReadImage(Prop(raw.Objects, "general", "imageUrl"));
                    break;
                case "pageNavigator":
                    spec.Type = VisualType.PageNavigator;
                    break;
                default:
                    spec.Type = VisualType.Unsupported;
                    style.Text = FriendlyName(raw.Type);
                    if (Prop(raw.ContainerObjects, "title", "text") is { } label && Literal(label) is { Length: > 0 } l) spec.Title = l;
                    break;
            }
            return spec;
        }

        void AddFields(VisualSpec spec, RawVisual raw)
        {
            if (spec.Type.IsAnalysis())
            {
                AddAnalysisFields(spec, raw);
                return;
            }
            foreach (var (role, json, label) in raw.Projections)
            {
                if (ToField(json, raw.Sources) is not { } field) continue;
                field.Label = label;
                var isCategory = role is "Category" or "Axis" or "Rows" or "Columns" or "Legend" && spec.Type != VisualType.Card
                    && !(role is "Columns" && spec.Type != VisualType.Table);
                var isValue = role is "Y" or "Values" or "Value" or "Data" or "Fields" or "Y2" or "ColumnY" or "LineY";
                if (spec.Type == VisualType.Table && role == "Values" && !field.IsSummarized)
                    isCategory = true;
                else if (!isCategory && !isValue)
                    continue;

                if (isCategory && !field.IsSummarized)
                {
                    // Charts show one category; Power BI's extra ones are drill-down levels.
                    if (spec.Type != VisualType.Table && spec.Category.Count > 0) continue;
                    if (spec.Category.Any(c => c.Table == field.Table && c.Column == field.Column)) continue;
                    spec.Category.Add(field);
                }
                else
                {
                    if (!field.IsSummarized && spec.Type != VisualType.Table) field.Aggregation = Aggregations.DefaultFor(field.Kind);
                    if (spec.Type is VisualType.Card or VisualType.PieChart or VisualType.DonutChart && spec.Values.Count > 0) continue;
                    spec.Values.Add(field);
                }
            }
        }

        /// <summary>
        /// Decomposition tree: Analyze is the value, Explain by the levels. Key influencers: Analyze is the
        /// target column, Explain by the fields that might influence it (summarized ones are read per row).
        /// </summary>
        void AddAnalysisFields(VisualSpec spec, RawVisual raw)
        {
            foreach (var (role, json, label) in raw.Projections)
            {
                if (ToField(json, raw.Sources) is not { } field) continue;
                field.Label = label;
                if (role is "Analyze" or "Target" && spec.Values.Count == 0)
                {
                    if (spec.Type == VisualType.KeyInfluencers) field.Aggregation = Aggregation.None;
                    else if (!field.IsSummarized) field.Aggregation = Aggregations.DefaultFor(field.Kind);
                    spec.Values.Add(field);
                }
                else if (role == "ExplainBy" && !field.IsMeasure)
                {
                    field.Aggregation = Aggregation.None;
                    if (!spec.Category.Any(c => c.Table == field.Table && c.Column == field.Column)) spec.Category.Add(field);
                }
            }
        }

        /// <summary>
        /// The expanded path of a decomposition tree. Power BI keeps it as the selectors of the tree's
        /// per-node settings; the longest one runs from the root to the deepest open node.
        /// </summary>
        List<string?>? ReadDrillPath(VisualSpec spec, RawVisual raw)
        {
            if (raw.Objects.ValueKind != JsonValueKind.Object) return null;
            List<string?>? best = null;
            foreach (var obj in raw.Objects.EnumerateObject())
            {
                if (obj.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var entry in obj.Value.EnumerateArray())
                {
                    if (!entry.TryGetProperty("selector", out var selector) || !selector.TryGetProperty("data", out var data)
                        || data.ValueKind != JsonValueKind.Array)
                        continue;
                    var path = new List<string?>();
                    foreach (var d in data.EnumerateArray())
                    {
                        if (!d.TryGetProperty("scopeId", out var scope) || !scope.TryGetProperty("Comparison", out var cmp)
                            || !cmp.TryGetProperty("Left", out var left) || !cmp.TryGetProperty("Right", out var right)
                            || ToField(left, raw.Sources) is not { } level)
                            break;
                        // Levels must follow the Explain by order.
                        if (path.Count >= spec.Category.Count || spec.Category[path.Count] is not { } expected
                            || expected.Table != level.Table || expected.Column != level.Column)
                            break;
                        path.Add(FilterValue(right));
                    }
                    if (path.Count > (best?.Count ?? 0)) best = path;
                }
            }
            return best;
        }

        FieldRef? ToField(JsonElement f, IReadOnlyDictionary<string, string> sources)
        {
            if (f.TryGetProperty("Column", out var column))
                return Lookup(Entity(column, sources), Str(column, "Property"), measure: false) is { } c ? new FieldRef(c, Aggregation.None) : null;
            if (f.TryGetProperty("Measure", out var measure))
                return Lookup(Entity(measure, sources), Str(measure, "Property"), measure: true) is { } m ? new FieldRef(m, Aggregation.None) : null;
            if (f.TryGetProperty("Aggregation", out var agg) && agg.TryGetProperty("Expression", out var inner)
                && inner.TryGetProperty("Column", out var aggColumn)
                && Lookup(Entity(aggColumn, sources), Str(aggColumn, "Property"), measure: false) is { } a)
            {
                var function = agg.TryGetProperty("Function", out var fn) && fn.TryGetInt32(out var n) ? n : 0;
                return new FieldRef(a, function switch
                {
                    1 or 6 => Aggregation.Average,
                    2 => Aggregation.CountDistinct,
                    3 => Aggregation.Min,
                    4 => Aggregation.Max,
                    5 => Aggregation.Count,
                    _ => a.IsNumeric ? Aggregation.Sum : Aggregation.Count,
                });
            }
            // A level of Power BI's automatic date hierarchy: the date column, bucketed.
            if (f.TryGetProperty("HierarchyLevel", out var level)
                && level.TryGetProperty("Expression", out var hExpr) && hExpr.TryGetProperty("Hierarchy", out var hierarchy)
                && hierarchy.TryGetProperty("Expression", out var hInner) && hInner.TryGetProperty("PropertyVariationSource", out var variation)
                && Lookup(Entity(variation, sources), Str(variation, "Property"), measure: false) is { } d)
            {
                var grain = Str(level, "Level") switch
                {
                    "Year" => DateGrain.Year,
                    "Quarter" => DateGrain.Quarter,
                    "Month" => DateGrain.Month,
                    "Day" => DateGrain.Day,
                    _ => DateGrain.None,
                };
                return new FieldRef(d, Aggregation.None) { Grain = d.Kind == FieldKind.Date ? grain : DateGrain.None };
            }
            return null;
        }

        /// <summary>The legacy layout stores filter lists as JSON inside a string.</summary>
        static JsonElement ParseEmbedded(JsonElement e, string name)
        {
            if (Str(e, name) is not { Length: > 0 } text) return default;
            try
            {
                using var doc = JsonDocument.Parse(text);
                return doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return default;
            }
        }

        /// <summary>
        /// Basic (categorical) filters: the field and the values it keeps. A filter with no condition
        /// is an empty card in Power BI's Filters pane, so it's kept as an inactive filter.
        /// </summary>
        List<FieldFilter> ReadFilters(JsonElement filters)
        {
            var result = new List<FieldFilter>();
            if (filters.ValueKind != JsonValueKind.Array) return result;
            foreach (var f in filters.EnumerateArray())
            {
                if (!f.TryGetProperty("field", out var fieldJson)) continue;
                var sources = new Dictionary<string, string>();
                f.TryGetProperty("filter", out var filter);
                if (filter.ValueKind == JsonValueKind.Object && filter.TryGetProperty("From", out var from))
                    foreach (var s in from.EnumerateArray())
                        if (Str(s, "Name") is { } alias && Str(s, "Entity") is { } entity) sources[alias] = entity;
                if (ToField(fieldJson, sources) is not { IsMeasure: false } field) continue;

                var values = new List<string?>();
                var rules = new List<FilterRule>();
                var matchAny = false;
                var supported = true;
                if (filter.ValueKind == JsonValueKind.Object && filter.TryGetProperty("Where", out var where))
                {
                    foreach (var w in where.EnumerateArray())
                    {
                        if (!w.TryGetProperty("Condition", out var cond)) continue;
                        if (cond.TryGetProperty("In", out var @in) && @in.TryGetProperty("Values", out var rows))
                        {
                            foreach (var row in rows.EnumerateArray())
                                if (row.ValueKind == JsonValueKind.Array && row.GetArrayLength() > 0)
                                    values.Add(FilterValue(row[0]));
                        }
                        else if (Rules(cond, rules, ref matchAny) is false)
                            supported = false;   // Not In, Top N, relative dates…: keep the card without a condition
                    }
                }
                if (result.Any(r => r.Field.Table == field.Table && r.Field.Column == field.Column)) continue;
                result.Add(supported
                    ? new FieldFilter { Field = field, Values = values, Rules = rules.Count > 0 ? rules : null, MatchAny = matchAny }
                    : new FieldFilter { Field = field });
            }
            return result;
        }

        /// <summary>
        /// An advanced filter's condition: one or two comparisons joined by And / Or, each possibly negated.
        /// Returns false for anything else.
        /// </summary>
        static bool Rules(JsonElement cond, List<FilterRule> rules, ref bool matchAny)
        {
            foreach (var (name, any) in new[] { ("And", false), ("Or", true) })
            {
                if (!cond.TryGetProperty(name, out var both)) continue;
                if (rules.Count > 0 && matchAny != any) return false;
                matchAny = any;
                return both.TryGetProperty("Left", out var l) && both.TryGetProperty("Right", out var r)
                    && Rules(l, rules, ref matchAny) && Rules(r, rules, ref matchAny);
            }
            if (Rule(cond) is not { } rule) return false;
            rules.Add(rule);
            return true;
        }

        static FilterRule? Rule(JsonElement cond)
        {
            var negate = false;
            if (cond.TryGetProperty("Not", out var not) && not.TryGetProperty("Expression", out var inner))
            {
                negate = true;
                cond = inner;
            }
            if (cond.TryGetProperty("Comparison", out var cmp) && cmp.TryGetProperty("Right", out var right))
            {
                var value = FilterValue(right);
                var kind = cmp.TryGetProperty("ComparisonKind", out var k) && k.TryGetInt32(out var n) ? n : 0;
                if (value is null)
                    return kind == 0 ? new FilterRule(negate ? FilterOperator.IsNotBlank : FilterOperator.IsBlank) : null;
                FilterOperator? op = (kind, negate) switch
                {
                    (0, false) => FilterOperator.Is,
                    (0, true) => FilterOperator.IsNot,
                    (1, false) or (4, true) => FilterOperator.GreaterThan,
                    (2, false) or (3, true) => FilterOperator.GreaterOrEqual,
                    (3, false) or (2, true) => FilterOperator.LessThan,
                    (4, false) or (1, true) => FilterOperator.LessOrEqual,
                    _ => null,
                };
                return op is { } o ? new FilterRule(o, value) : null;
            }
            foreach (var (name, yes, no) in new[]
            {
                ("Contains", FilterOperator.Contains, FilterOperator.DoesNotContain),
                ("StartsWith", FilterOperator.StartsWith, FilterOperator.DoesNotStartWith),
            })
            {
                if (cond.TryGetProperty(name, out var c) && c.TryGetProperty("Right", out var r) && FilterValue(r) is { } v)
                    return new FilterRule(negate ? no : yes, v);
            }
            return null;
        }

        /// <summary>A filter literal as the text DuckDB gives the value when cast to VARCHAR.</summary>
        static string? FilterValue(JsonElement literal)
        {
            if (!literal.TryGetProperty("Literal", out var l) || Str(l, "Value") is not { } text || text == "null") return null;
            if (text.StartsWith("datetime'") && DateTime.TryParse(text[9..^1], CultureInfo.InvariantCulture, out var dt))
                return dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            return Literal(literal) is { } v && double.TryParse(v, CultureInfo.InvariantCulture, out var d) && !text.StartsWith('\'')
                ? d.ToString("0.0###############", CultureInfo.InvariantCulture)   // DuckDB prints doubles with a decimal point
                : Literal(literal);
        }

        static string? Entity(JsonElement field, IReadOnlyDictionary<string, string> sources)
        {
            if (!field.TryGetProperty("Expression", out var expr) || !expr.TryGetProperty("SourceRef", out var source)) return null;
            return Str(source, "Entity") ?? (Str(source, "Source") is { } alias ? sources.GetValueOrDefault(alias) : null);
        }

        FieldInfo? Lookup(string? table, string? name, bool measure)
        {
            if (table is null || name is null) return null;
            return tables.FirstOrDefault(t => string.Equals(t.Name, table, StringComparison.OrdinalIgnoreCase))?.Fields
                .FirstOrDefault(f => f.IsMeasure == measure && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        void ReadText(JsonElement objects, VisualStyle style, double scale)
        {
            if (Prop(objects, "general", "paragraphs") is not { ValueKind: JsonValueKind.Array } paragraphs) return;
            var lines = new List<string>();
            foreach (var p in paragraphs.EnumerateArray())
            {
                if (!p.TryGetProperty("textRuns", out var runs)) continue;
                var line = new StringBuilder();
                foreach (var run in runs.EnumerateArray())
                {
                    line.Append(Str(run, "value"));
                    if (style.FontSize is not null || !run.TryGetProperty("textStyle", out var ts)) continue;
                    // Power BI sizes text in points; the page is in pixels.
                    if (Str(ts, "fontSize") is { } size && double.TryParse(size.TrimEnd('p', 't', 'x'), CultureInfo.InvariantCulture, out var pt))
                        style.FontSize = Math.Round((size.EndsWith("px") ? pt : pt * 96 / 72) * scale, 1);
                    style.Foreground = Theme.Hex(Str(ts, "color"));
                    style.Bold = Str(ts, "fontWeight") is "bold" or "bolder" || Str(ts, "fontFamily")?.Contains("Bold") == true;
                }
                lines.Add(line.ToString());
            }
            style.Text = string.Join("\n", lines);
        }

        string? ReadImage(JsonElement? url)
        {
            if (url is not { } u || !u.TryGetProperty("expr", out var expr) || !expr.TryGetProperty("ResourcePackageItem", out var item)
                || Str(item, "ItemName") is not { } name)
                return null;
            var entry = zip.GetEntry("Report/StaticResources/RegisteredResources/" + name);
            if (entry is null || entry.Length > 8 << 20) return null;
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return Convert.ToBase64String(buffer.ToArray());
        }

        JsonDocument? Json(string name)
        {
            if (zip.GetEntry(name) is not { } entry) return null;
            using var stream = entry.Open();
            return JsonDocument.Parse(stream);
        }
    }

    /// <summary>The report theme's colours, for resolving "theme colour 3, 50% darker" references.</summary>
    sealed class Theme
    {
        // Power BI's default data colours, for reports without a theme file.
        string[] _data = ["#118DFF", "#12239E", "#E66C37", "#6B007B", "#E044A7", "#744EC2", "#D9B300", "#D64550"];
        readonly Dictionary<string, string> _named = new(StringComparer.OrdinalIgnoreCase)
        {
            ["background"] = "#FFFFFF", ["foreground"] = "#252423",
        };

        /// <summary>The page colour: the base theme's, blended over the area around the page by its transparency.</summary>
        public string? PageBackground { get; private set; }

        public static Theme Load(ZipArchive zip)
        {
            var theme = new Theme();
            // Power BI Desktop ships its Fluent 2 base theme, so files often name it without including it.
            if (zip.GetEntry("Report/definition/report.json") is { } report)
            {
                using var reader = new StreamReader(report.Open());
                if (reader.ReadToEnd().Contains("\"Fluent2")) theme.PageBackground = "#F7F7F7";
            }
            // A custom theme overrides the base theme, so read it last.
            var files = zip.Entries.Where(e => e.FullName.EndsWith(".json") && e.FullName.Contains("/BaseThemes/"))
                .Concat(zip.Entries.Where(e => e.FullName.EndsWith(".json") && e.FullName.Contains("/RegisteredResources/")));
            foreach (var entry in files)
            {
                try
                {
                    using var stream = entry.Open();
                    using var doc = JsonDocument.Parse(stream);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    if (root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String) theme._name = name.GetString();
                    if (root.TryGetProperty("dataColors", out var data) && data.ValueKind == JsonValueKind.Array)
                        theme._data = data.EnumerateArray().Select(c => Hex(c.GetString()) ?? "#000000").ToArray();
                    foreach (var p in root.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.String && Hex(p.Value.GetString()) is { } hex) theme._named[p.Name] = hex;
                    theme.ReadPageStyle(root);
                }
                catch (JsonException)
                {
                    // Not a theme.
                }
            }
            return theme;
        }

        public ReportTheme ToReportTheme() => new()
        {
            Name = _name ?? "Report theme", DataColors = [.. _data],
            Background = _named["background"], Foreground = _named["foreground"], PageBackground = PageBackground,
        };

        string? _name;

        /// <summary>Resolves a fill such as <c>{"solid":{"color":{"expr":…}}}</c> to #RRGGBB.</summary>
        public string? Color(JsonElement? fill)
        {
            if (fill is not { ValueKind: JsonValueKind.Object } f) return null;
            if (!f.TryGetProperty("solid", out var solid) || !solid.TryGetProperty("color", out var color)) return null;
            if (color.ValueKind == JsonValueKind.String) return Hex(color.GetString());
            if (!color.TryGetProperty("expr", out var expr)) return null;
            if (expr.TryGetProperty("ThemeDataColor", out var themed))
            {
                var id = themed.TryGetProperty("ColorId", out var i) && i.TryGetInt32(out var n) ? n : 0;
                var percent = themed.TryGetProperty("Percent", out var pc) && pc.TryGetDouble(out var d) ? d : 0;
                // Ids 0 and 1 are the background and foreground; data colours start at 2.
                var hex = id switch
                {
                    0 => _named["background"],
                    1 => _named["foreground"],
                    _ => _data.Length > 0 ? _data[(id - 2) % _data.Length] : "#000000",
                };
                return Shade(hex, percent);
            }
            if (Literal(color) is { } literal)
                return Hex(literal) ?? _named.GetValueOrDefault(literal);
            return null;
        }

        /// <summary>visualStyles.page.*: background colour and transparency over the outspace colour.</summary>
        void ReadPageStyle(JsonElement root)
        {
            if (!root.TryGetProperty("visualStyles", out var styles) || !styles.TryGetProperty("page", out var page)
                || !page.TryGetProperty("*", out var all))
                return;
            string? ColorOf(string obj, out double transparency)
            {
                transparency = 0;
                if (!all.TryGetProperty(obj, out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0) return null;
                var first = list[0];
                if (first.TryGetProperty("transparency", out var t) && t.TryGetDouble(out var d)) transparency = d / 100;
                return first.TryGetProperty("color", out var c) ? Color(c) : null;
            }
            var background = ColorOf("background", out var transparency);
            if (background is null) return;
            var outspace = ColorOf("outspace", out _) ?? "#FFFFFF";
            var (bg, os) = (Convert.FromHexString(background[1..]), Convert.FromHexString(outspace[1..]));
            for (var i = 0; i < 3; i++) bg[i] = (byte)Math.Round(bg[i] * (1 - transparency) + os[i] * transparency);
            PageBackground = "#" + Convert.ToHexString(bg);
        }

        /// <summary>Positive percentages blend toward white, negative toward black.</summary>
        static string Shade(string hex, double percent)
        {
            var rgb = Convert.FromHexString(hex[1..7]);
            for (var i = 0; i < 3; i++)
                rgb[i] = (byte)Math.Round(percent >= 0 ? rgb[i] + (255 - rgb[i]) * percent : rgb[i] * (1 + percent));
            return "#" + Convert.ToHexString(rgb);
        }

        public static string? Hex(string? value)
        {
            if (value is null || !value.StartsWith('#')) return null;
            var digits = value[1..];
            if (digits.Length == 3) digits = string.Concat(digits.Select(c => $"{c}{c}"));
            return digits.Length is 6 or 8 && digits.All(Uri.IsHexDigit) ? "#" + digits[..6].ToUpperInvariant() : null;
        }
    }

    // ---------------------------------------------------------- JSON helpers

    static JsonElement Child(JsonElement e, string name) => e.TryGetProperty(name, out var c) ? c.Clone() : default;

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static double Num(JsonElement e, string name, double fallback = 0) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;

    static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>A formatting property: the first entry of <c>objects[obj]</c> that sets it, skipping per-series overrides.</summary>
    static JsonElement? Prop(JsonElement objects, string obj, string prop)
    {
        if (objects.ValueKind != JsonValueKind.Object || !objects.TryGetProperty(obj, out var entries)
            || entries.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.TryGetProperty("selector", out var selector) && Str(selector, "id") != "default") continue;
            if (entry.TryGetProperty("properties", out var props) && props.TryGetProperty(prop, out var value)) return value;
        }
        return null;
    }

    /// <summary>The value of <c>{"expr":{"Literal":{"Value":"'text'"}}}</c>, without quotes or type suffixes (12D, 0L).</summary>
    static string? Literal(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.Object } v) return null;
        if (v.TryGetProperty("expr", out var expr)) v = expr;
        if (!v.TryGetProperty("Literal", out var literal) || Str(literal, "Value") is not { } text) return null;
        if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'') return text[1..^1].Replace("''", "'");
        if (text.Length > 1 && text[^1] is 'D' or 'L' or 'M' && double.TryParse(text[..^1], CultureInfo.InvariantCulture, out _))
            return text[..^1];
        return text;
    }
}
