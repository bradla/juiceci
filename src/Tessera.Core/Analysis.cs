namespace Tessera.Core;

/// <summary>One bar of a decomposition tree level. <see cref="Key"/> is the value as filters compare it (null is blank).</summary>
public sealed record DecompositionNode(string? Key, string Label, double Amount);

/// <param name="Selected">Index of the expanded node in <see cref="Nodes"/>, or -1.</param>
/// <param name="Parent">The amount of the node this level breaks down, which full-length bars stand for.</param>
public sealed record DecompositionLevel(FieldRef Field, IReadOnlyList<DecompositionNode> Nodes, int Selected, double Parent, int Hidden);

/// <summary>A decomposition tree: the total of a value, broken down one field at a time along the expanded path.</summary>
public sealed record Decomposition(string ValueName, string? Format, double Total, IReadOnlyList<DecompositionLevel> Levels)
{
    /// <summary>Explain-by fields after the last level, available to expand into next.</summary>
    public IReadOnlyList<FieldRef> Remaining { get; init; } = [];
}

/// <summary>The likelihood of the target value at one value (or bin) of an explaining field.</summary>
public sealed record InfluencePoint(string Label, double X, double Count, double Probability);

/// <summary>How the likelihood of the target value varies across one explaining field.</summary>
public sealed record FieldBreakdown(FieldRef Field, string Name, bool IsNumeric, IReadOnlyList<InfluencePoint> Points)
{
    /// <summary>For numeric fields: the fitted logistic curve's intercept and slope, per unit of the field.</summary>
    public (double Intercept, double Slope)? Fit { get; init; }
}

/// <summary>
/// "When <see cref="Condition"/>, the likelihood of the target being its value increases by <see cref="Ratio"/>x."
/// </summary>
public sealed record Influencer(FieldBreakdown Breakdown, string Condition, string? Key, double Ratio, double Count, double Probability);

public sealed record KeyInfluencerAnalysis(
    string TargetName, string TargetValue, IReadOnlyList<string> TargetValues, double Rows, double Probability,
    IReadOnlyList<Influencer> Influencers, IReadOnlyList<FieldBreakdown> Fields);

/// <summary>Power BI's AI visuals, computed from ordinary grouped queries.</summary>
public static class Analysis
{
    const int RowLimit = 100_000;

    /// <param name="limit">Bars per level; the rest are counted in <see cref="DecompositionLevel.Hidden"/>.</param>
    public static Decomposition Decompose(DataEngine engine, VisualSpec spec, IEnumerable<FieldFilter>? pageFilters, int limit = 10)
    {
        if (spec.Values.Count == 0) throw new InvalidOperationException("Add a value to analyze.");
        var value = spec.Values[0];
        var page = pageFilters?.ToList() ?? [];
        var totalRow = engine.Query(new VisualSpec { Type = VisualType.Card, Values = [value], Filters = spec.Filters }, 1, page).Rows;
        var total = totalRow.Count > 0 ? Sql.ToDouble(totalRow[0][0]) ?? 0 : 0;

        var levels = new List<DecompositionLevel>();
        var filters = new List<FieldFilter>(spec.Filters);
        var parent = total;
        var drill = spec.Drill ?? [];
        for (var k = 0; k < spec.Category.Count && k <= drill.Count; k++)
        {
            var field = spec.Category[k];
            var result = engine.Query(new VisualSpec { Type = VisualType.BarChart, Category = [field], Values = [value], Filters = filters },
                500, page);
            var nodes = result.Rows
                .Select(r => new DecompositionNode(Sql.FilterText(r[0]), r[0] is null ? "(Blank)" : Sql.Format(r[0]), Sql.ToDouble(r[1]) ?? 0))
                .ToList();
            var selected = k < drill.Count ? nodes.FindIndex(n => n.Key == drill[k]) : -1;
            // Keep the expanded node in view even when it's further down than the bars shown.
            var shown = nodes.Take(limit).ToList();
            if (selected >= limit)
            {
                shown[^1] = nodes[selected];
                selected = limit - 1;
            }
            levels.Add(new DecompositionLevel(field, shown, selected, parent, nodes.Count - shown.Count));
            if (selected < 0) break;
            parent = shown[selected].Amount;
            filters.Add(new FieldFilter { Field = field, Values = [shown[selected].Key] });
        }
        return new Decomposition(value.DisplayName, value.Aggregation is Aggregation.Count or Aggregation.CountDistinct ? "#,0" : value.FormatString,
            total, levels) { Remaining = spec.Category.Skip(levels.Count).ToList() };
    }

    /// <summary>
    /// What makes rows of the target field more likely to have the target value. Text fields compare each
    /// value's likelihood with the rest of the rows; numeric fields fit a logistic curve and report the change
    /// in odds over a typical (one standard deviation) increase or decrease, as Power BI does.
    /// </summary>
    public static KeyInfluencerAnalysis KeyInfluencers(DataEngine engine, VisualSpec spec, IEnumerable<FieldFilter>? pageFilters)
    {
        if (spec.Values.Count == 0) throw new InvalidOperationException("Add a field to analyze.");
        var target = new FieldRef { Table = spec.Values[0].Table, Column = spec.Values[0].Column, Kind = spec.Values[0].Kind };
        var page = pageFilters?.ToList() ?? [];
        var count = new FieldRef { Table = target.Table, Column = target.Column, Kind = target.Kind, Aggregation = Aggregation.Count };

        var byTarget = engine.Query(new VisualSpec { Type = VisualType.BarChart, Category = [target], Values = [count], Filters = spec.Filters }, 200, page);
        var targetValues = byTarget.Rows.Where(r => r[0] is not null).Select(r => Sql.FilterText(r[0])!).ToList();
        if (targetValues.Count == 0) throw new InvalidOperationException($"{target.Column} has no values to explain.");
        var targetValue = spec.TargetValue is { } t && targetValues.Contains(t) ? t : targetValues[0];
        var rows = byTarget.Rows.Sum(r => Sql.ToDouble(r[1]) ?? 0);
        var hits = byTarget.Rows.Where(r => Sql.FilterText(r[0]) == targetValue).Sum(r => Sql.ToDouble(r[1]) ?? 0);
        var probability = rows > 0 ? hits / rows : 0;

        var withTarget = new List<FieldFilter>(spec.Filters) { new() { Field = target, Values = [targetValue] } };
        var fields = new List<FieldBreakdown>();
        var influencers = new List<Influencer>();
        foreach (var f in spec.Category)
        {
            var field = new FieldRef { Table = f.Table, Column = f.Column, Kind = f.Kind, FormatString = f.FormatString };
            if (field.Table == target.Table && field.Column == target.Column) continue;
            if (fields.Any(b => b.Field.Table == field.Table && b.Field.Column == field.Column)) continue;

            QueryResult Counts(List<FieldFilter> filters) =>
                engine.Query(new VisualSpec { Type = VisualType.Table, Category = [field], Values = [count], Filters = filters }, RowLimit, page);
            var all = Counts(spec.Filters);
            var won = Counts(withTarget).Rows.ToDictionary(r => Sql.FilterText(r[0]) ?? "\0", r => Sql.ToDouble(r[1]) ?? 0);
            var groups = all.Rows.Select(r =>
            {
                var key = Sql.FilterText(r[0]);
                return (Key: key, Raw: r[0], N: Sql.ToDouble(r[1]) ?? 0, Hits: won.GetValueOrDefault(key ?? "\0"));
            }).Where(g => g.N > 0).ToList();
            var name = f.Label ?? f.Column;

            if (field.Kind == FieldKind.Number)
            {
                var numeric = groups.Where(g => g.Raw is not null).Select(g => (X: Sql.ToDouble(g.Raw)!.Value, g.N, g.Hits)).ToList();
                if (numeric.Count < 2) continue;
                var breakdown = new FieldBreakdown(field, name, true, Bins(numeric, field.FormatString));
                if (Logistic(numeric) is not { } fit) continue;
                breakdown = breakdown with { Fit = (fit.Intercept, fit.Slope) };
                fields.Add(breakdown);
                if (Math.Abs(fit.Z) < 2 || fit.Slope == 0) continue;
                var step = RoundStep(fit.Sd);
                var up = fit.Slope > 0;
                // Like Power BI, the change in odds over a typical step up (or down).
                double P(double x) => 1 / (1 + Math.Exp(-(fit.Intercept + fit.Slope * x)));
                var ratio = Math.Exp(Math.Abs(fit.Slope) * step);
                var stepText = Sql.FormatNumber(step, field.FormatString is { } fs && fs.Contains('%') ? null : field.FormatString, compact: true);
                influencers.Add(new Influencer(breakdown, $"{name} goes {(up ? "up" : "down")} {stepText}", null, ratio,
                    numeric.Sum(g => g.N), P(fit.Mean + (up ? step : -step))));
            }
            else
            {
                var points = groups.Select(g => new InfluencePoint(g.Raw is null ? "(Blank)" : Sql.Format(g.Raw), 0, g.N, g.Hits / g.N)).ToList();
                var breakdown = new FieldBreakdown(field, name, false, points.OrderByDescending(p => p.Probability).ToList());
                fields.Add(breakdown);
                var total = groups.Sum(g => g.N);
                var totalHits = groups.Sum(g => g.Hits);
                foreach (var g in groups)
                {
                    var rest = total - g.N;
                    if (rest <= 0 || g.N < Math.Max(10, total * 0.005)) continue;
                    var p = g.Hits / g.N;
                    var q = (totalHits - g.Hits) / rest;
                    if (q <= 0 || p <= q) continue;
                    // Two-proportion z-test, so small differences from small groups don't count.
                    var pooled = totalHits / total;
                    var se = Math.Sqrt(pooled * (1 - pooled) * (1 / g.N + 1 / rest));
                    if (se == 0 || (p - q) / se < 2) continue;
                    var label = g.Raw is null ? "(Blank)" : Sql.Format(g.Raw);
                    influencers.Add(new Influencer(breakdown, $"{name} is {label}", g.Key, p / q, g.N, p));
                }
            }
        }
        return new KeyInfluencerAnalysis(spec.Values[0].Label ?? target.Column, targetValue, targetValues, rows, probability,
            influencers.OrderByDescending(i => i.Ratio).ToList(), fields);
    }

    /// <summary>Points for the detail chart: every value when there are few, otherwise equal-width bins.</summary>
    static List<InfluencePoint> Bins(List<(double X, double N, double Hits)> groups, string? format)
    {
        if (groups.Count <= 60)
            return groups.OrderBy(g => g.X).Select(g => new InfluencePoint(Sql.FormatNumber(g.X, format, compact: true), g.X, g.N, g.Hits / g.N)).ToList();
        var (min, max) = (groups.Min(g => g.X), groups.Max(g => g.X));
        const int bins = 30;
        var width = (max - min) / bins;
        return groups.GroupBy(g => Math.Min(bins - 1, (int)((g.X - min) / width)))
            .Select(b =>
            {
                var n = b.Sum(g => g.N);
                var x = b.Sum(g => g.X * g.N) / n;
                return new InfluencePoint(Sql.FormatNumber(x, format, compact: true), x, n, b.Sum(g => g.Hits) / n);
            })
            .OrderBy(p => p.X).ToList();
    }

    /// <summary>A one-standard-deviation step, rounded to one significant figure (0.0234 → 0.02).</summary>
    static double RoundStep(double sd)
    {
        if (sd <= 0) return 1;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(sd)));
        return Math.Round(sd / magnitude) * magnitude;
    }

    /// <summary>Weighted logistic regression of hits on x by Newton's method; null if it doesn't converge.</summary>
    static (double Intercept, double Slope, double Z, double Mean, double Sd)? Logistic(List<(double X, double N, double Hits)> groups)
    {
        var n = groups.Sum(g => g.N);
        var mean = groups.Sum(g => g.X * g.N) / n;
        var sd = Math.Sqrt(groups.Sum(g => g.N * (g.X - mean) * (g.X - mean)) / n);
        if (sd == 0 || double.IsNaN(sd)) return null;

        // Fit on standardized x for stability, then convert back.
        double a = 0, b = 0, haa = 0, hab = 0, hbb = 0;
        for (var iteration = 0; iteration < 50; iteration++)
        {
            double ga = 0, gb = 0;
            haa = hab = hbb = 0;
            foreach (var (x, count, hits) in groups)
            {
                var z = (x - mean) / sd;
                var p = 1 / (1 + Math.Exp(-(a + b * z)));
                var w = count * p * (1 - p);
                ga += hits - count * p;
                gb += (hits - count * p) * z;
                haa += w;
                hab += w * z;
                hbb += w * z * z;
            }
            var det = haa * hbb - hab * hab;
            if (det <= 1e-12) return null;
            var da = (hbb * ga - hab * gb) / det;
            var db = (haa * gb - hab * ga) / det;
            a += da;
            b += db;
            if (Math.Abs(da) + Math.Abs(db) < 1e-9) break;
        }
        if (double.IsNaN(a) || double.IsNaN(b)) return null;
        var se = Math.Sqrt(haa / (haa * hbb - hab * hab));
        return (a - b * mean / sd, b / sd, b / se, mean, sd);
    }
}
