using System.Text;

namespace Tessera.Core;

/// <summary>Turns a visual's field wells into a single SQL query.</summary>
public static class QueryBuilder
{
    public static string Build(VisualSpec visual, int limit = 1000) => Build(visual, new SemanticModel(), limit);

    /// <param name="pageFilters">Filters from the visual's page; the visual's own filters are added to them.</param>
    public static string Build(VisualSpec visual, SemanticModel model, int limit = 1000, IEnumerable<FieldFilter>? pageFilters = null)
    {
        var fields = visual.Category.Concat(visual.Values).ToList();
        if (fields.Count == 0)
            throw new InvalidOperationException("The visual has no fields.");

        // "Don't summarize" values behave like extra grouping columns.
        var groups = Groups(visual);
        var values = Summarized(visual);

        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var aggregated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups) tables.Add(g.Table);

        var select = new List<string>();
        for (var i = 0; i < groups.Count; i++)
            select.Add($"{GroupExpression(groups[i])} AS c{i}");
        for (var i = 0; i < values.Count; i++)
        {
            var v = values[i];
            if (v.IsMeasure)
            {
                var measure = model.FindMeasure(v.Table, v.Column)
                    ?? throw new InvalidOperationException($"Measure [{v.Column}] isn't in this report's model.");
                var compiled = DaxTranslator.Translate(measure, model);
                tables.UnionWith(compiled.Tables);
                aggregated.UnionWith(compiled.AggregatedTables);
                select.Add($"{compiled.Sql} AS m{i}");
            }
            else
            {
                tables.Add(v.Table);
                aggregated.Add(v.Table);
                select.Add($"{Expression(v)} AS m{i}");
            }
        }

        // A grouped column with a sort-by column (month names by number) is ordered by that column instead.
        var sortKeys = groups.Select(g => g.Grain == DateGrain.None && model.FindSortRule(g.Table, g.Column) is { } rule
            ? $"min({Sql.Ident(g.Table)}.{Sql.Ident(rule.ByColumn)})" : null).ToList();

        var sql = new StringBuilder("SELECT ");
        sql.Append(string.Join(", ", select));
        var filters = visual.Filters.Concat(pageFilters ?? []).Where(f => f.IsActive).ToList();
        sql.Append(" FROM ").Append(From(model, tables, aggregated, filters, out var applied));
        if (applied.Count > 0)
            sql.Append(" WHERE ").Append(string.Join(" AND ", applied.Select(Condition)));
        if (groups.Count > 0)
            sql.Append(" GROUP BY ").Append(string.Join(", ", Enumerable.Range(1, groups.Count)));

        var chronological = groups.Count > 0 && groups[0].Kind == FieldKind.Date;
        var orderBy = OrderBy(visual.Type, groups.Count, values.Count, chronological, sortKeys);
        if (orderBy is not null) sql.Append(" ORDER BY ").Append(orderBy);
        sql.Append(" LIMIT ").Append(limit);
        return sql.ToString();
    }

    static List<FieldRef> Groups(VisualSpec visual) =>
        visual.Category.Concat(visual.Values.Where(v => !v.IsSummarized)).ToList();

    static List<FieldRef> Summarized(VisualSpec visual) => visual.Values.Where(v => v.IsSummarized).ToList();

    /// <summary>Column headers in the same order as <see cref="Build"/>'s output.</summary>
    public static IReadOnlyList<string> Headers(VisualSpec visual) =>
        Groups(visual).Concat(Summarized(visual)).Select(f => f.DisplayName).ToList();

    /// <summary>Format strings in the same order as <see cref="Headers"/>.</summary>
    public static IReadOnlyList<string?> Formats(VisualSpec visual) =>
        Groups(visual).Concat(Summarized(visual))
            .Select(f => f.Aggregation is Aggregation.Count or Aggregation.CountDistinct ? "#,0" : f.FormatString).ToList();

    public static int GroupCount(VisualSpec visual) => Groups(visual).Count;

    /// <summary>
    /// The FROM clause. Rows come from the one table whose rows are summarized (the "fact" table),
    /// joined along many-to-one relationships to every other table the visual reads, as Power BI's
    /// filter propagation would.
    /// </summary>
    /// <param name="applied">The filters whose table the query can reach; Power BI ignores the others too.</param>
    static string From(SemanticModel model, HashSet<string> tables, HashSet<string> aggregated,
        List<FieldFilter> filters, out List<FieldFilter> applied)
    {
        applied = [];
        if (tables.Count == 0 && aggregated.Count == 0) return "(SELECT 1) AS one";
        if (aggregated.Count > 1)
            throw new NotSupportedException(
                $"Values summarize rows of {string.Join(" and ", aggregated.Order())}; a visual can summarize one table at a time.");

        // Prefer the summarized table, then any table the visual uses, then any table that links them.
        var candidates = aggregated.Count == 1 ? aggregated.ToList()
            : tables.Concat(model.Relationships.Select(r => r.FromTable)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var root in candidates)
        {
            var joins = JoinPath(model, root, tables);
            if (joins is null) continue;
            var needed = new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase);
            foreach (var f in filters)
            {
                if (JoinPath(model, root, [f.Field.Table]) is null) continue;
                applied.Add(f);
                needed.Add(f.Field.Table);
            }
            joins = JoinPath(model, root, needed)!;
            var sql = new StringBuilder(Sql.Ident(root));
            foreach (var r in joins)
            {
                sql.Append(" LEFT JOIN ").Append(Sql.Ident(r.ToTable)).Append(" ON ")
                    .Append(Sql.Ident(r.FromTable)).Append('.').Append(Sql.Ident(r.FromColumn)).Append(" = ")
                    .Append(Sql.Ident(r.ToTable)).Append('.').Append(Sql.Ident(r.ToColumn));
            }
            return sql.ToString();
        }

        var others = tables.Except(aggregated, StringComparer.OrdinalIgnoreCase).Order().ToList();
        throw new NotSupportedException(aggregated.Count == 1
            ? $"No relationship links {aggregated.First()} to {string.Join(", ", others)}."
            : $"Fields come from {string.Join(", ", tables.Order())}, which no relationship links.");
    }

    /// <summary>Breadth-first walk from <paramref name="root"/> along active relationships; null if a table can't be reached.</summary>
    static List<Relationship>? JoinPath(SemanticModel model, string root, HashSet<string> needed)
    {
        var via = new Dictionary<string, Relationship?>(StringComparer.OrdinalIgnoreCase) { [root] = null };
        var queue = new Queue<string>([root]);
        while (queue.Count > 0)
        {
            var table = queue.Dequeue();
            foreach (var r in model.Relationships)
            {
                if (!r.IsActive || !string.Equals(r.FromTable, table, StringComparison.OrdinalIgnoreCase) || via.ContainsKey(r.ToTable))
                    continue;
                via[r.ToTable] = r;
                queue.Enqueue(r.ToTable);
            }
        }
        if (!needed.All(via.ContainsKey)) return null;

        // Keep only the joins on the way to a needed table, in an order where each join's source comes first.
        var used = new List<Relationship>();
        foreach (var table in needed)
            for (var r = via[table]; r is not null && !used.Contains(r); r = via[r.FromTable])
                used.Add(r);
        var order = via.Keys.Select((t, i) => (t, i)).ToDictionary(x => x.t, x => x.i, StringComparer.OrdinalIgnoreCase);
        return used.OrderBy(r => order[r.ToTable]).ToList();
    }

    static string Condition(FieldFilter f)
    {
        var col = Col(f.Field);
        var conditions = new List<string>();
        if (f.Values.Count > 0)
        {
            var values = f.Values.Where(v => v is not null).Select(v => Sql.Literal(v!)).ToList();
            var parts = new List<string>();
            if (values.Count > 0) parts.Add($"CAST({col} AS VARCHAR) IN ({string.Join(", ", values)})");
            if (f.Values.Contains(null)) parts.Add($"{col} IS NULL");
            conditions.Add("(" + string.Join(" OR ", parts) + ")");
        }
        if (f.Rules is { Count: > 0 } rules)
            conditions.Add("(" + string.Join(f.MatchAny ? " OR " : " AND ", rules.Select(r => Condition(f.Field, r))) + ")");
        return string.Join(" AND ", conditions);
    }

    static string Condition(FieldRef field, FilterRule rule)
    {
        var col = Col(field);
        var text = $"CAST({col} AS VARCHAR)";
        // Compare numbers and dates as such; text compares case-insensitively, as Power BI does.
        var value = rule.Value is null ? "NULL"
            : field.Kind == FieldKind.Number && double.TryParse(rule.Value, System.Globalization.CultureInfo.InvariantCulture, out var d)
                ? d.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : field.Kind == FieldKind.Date ? $"CAST({Sql.Literal(rule.Value)} AS TIMESTAMP)"
            : Sql.Literal(rule.Value);
        var lhs = field.Kind is FieldKind.Number or FieldKind.Date ? col : $"lower({text})";
        var rhs = field.Kind is FieldKind.Number or FieldKind.Date ? value : $"lower({value})";
        var like = Sql.Literal((rule.Value ?? "").ToLowerInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_"));
        return rule.Operator switch
        {
            FilterOperator.Is => $"{lhs} = {rhs}",
            FilterOperator.IsNot => $"({lhs} <> {rhs} OR {col} IS NULL)",
            FilterOperator.LessThan => $"{lhs} < {rhs}",
            FilterOperator.LessOrEqual => $"{lhs} <= {rhs}",
            FilterOperator.GreaterThan => $"{lhs} > {rhs}",
            FilterOperator.GreaterOrEqual => $"{lhs} >= {rhs}",
            FilterOperator.Contains => $"lower({text}) LIKE ('%' || {like} || '%') ESCAPE '\\'",
            FilterOperator.DoesNotContain => $"COALESCE(lower({text}) NOT LIKE ('%' || {like} || '%') ESCAPE '\\', true)",
            FilterOperator.StartsWith => $"lower({text}) LIKE ({like} || '%') ESCAPE '\\'",
            FilterOperator.DoesNotStartWith => $"COALESCE(lower({text}) NOT LIKE ({like} || '%') ESCAPE '\\', true)",
            FilterOperator.IsBlank => field.Kind == FieldKind.Text ? $"({col} IS NULL OR {col} = '')" : $"{col} IS NULL",
            _ => field.Kind == FieldKind.Text ? $"({col} IS NOT NULL AND {col} <> '')" : $"{col} IS NOT NULL",
        };
    }

    static string Col(FieldRef f) => $"{Sql.Ident(f.Table)}.{Sql.Ident(f.Column)}";

    // Bucketed dates come back as text that sorts chronologically ("2024-03", "2024 Q1").
    static string GroupExpression(FieldRef f)
    {
        var col = Col(f);
        if (f.Kind != FieldKind.Date) return col;
        return f.Grain switch
        {
            DateGrain.Year => $"CAST(year({col}) AS VARCHAR)",
            DateGrain.Quarter => $"CAST(year({col}) AS VARCHAR) || ' Q' || CAST(quarter({col}) AS VARCHAR)",
            DateGrain.Month => $"strftime({col}, '%Y-%m')",
            DateGrain.Day => $"CAST({col} AS DATE)",
            _ => col,
        };
    }

    static string Expression(FieldRef f)
    {
        var col = Col(f);
        return f.Aggregation switch
        {
            Aggregation.Sum => $"CAST(sum({col}) AS DOUBLE)",
            Aggregation.Average => $"CAST(avg({col}) AS DOUBLE)",
            Aggregation.Min => f.IsNumeric() ? $"CAST(min({col}) AS DOUBLE)" : $"min({col})",
            Aggregation.Max => f.IsNumeric() ? $"CAST(max({col}) AS DOUBLE)" : $"max({col})",
            Aggregation.Count => $"CAST(count({col}) AS DOUBLE)",
            Aggregation.CountDistinct => $"CAST(count(DISTINCT {col}) AS DOUBLE)",
            _ => throw new ArgumentOutOfRangeException(nameof(f), f.Aggregation, null),
        };
    }

    static bool IsNumeric(this FieldRef f) => f.Kind == FieldKind.Number;

    static string? OrderBy(VisualType type, int groups, int measures, bool chronological, List<string?> sortKeys)
    {
        var firstMeasure = groups + 1;
        var allGroups = string.Join(", ", Enumerable.Range(1, groups).Select(i => $"{sortKeys[i - 1] ?? i.ToString()} NULLS LAST"));
        return type switch
        {
            VisualType.BarChart or VisualType.ColumnChart or VisualType.PieChart
                when measures > 0 && groups > 0 && !chronological && sortKeys[0] is null
                => $"{firstMeasure} DESC NULLS LAST",
            _ when groups > 0 => allGroups,
            _ => null,
        };
    }
}
