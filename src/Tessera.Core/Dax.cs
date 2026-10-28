using System.Globalization;
using System.Text;

namespace Tessera.Core;

/// <summary>A measure translated to one SQL expression over the visual's joined rows.</summary>
/// <param name="Sql">An aggregate expression, valid in a SELECT with GROUP BY.</param>
/// <param name="AggregatedTables">Tables whose rows the measure iterates; the query must run over that table.</param>
/// <param name="Tables">Every table the expression reads a column from.</param>
public sealed record CompiledMeasure(string Sql, IReadOnlySet<string> AggregatedTables, IReadOnlySet<string> Tables);

/// <summary>
/// Translates the everyday subset of DAX to DuckDB SQL: aggregations and their X iterators,
/// CALCULATE and FILTER with row conditions, DIVIDE, IF, VAR/RETURN, arithmetic and references to
/// other measures. Filters become aggregate FILTER (WHERE …) clauses, so a measure stays a single
/// expression that respects the visual's grouping. Anything that changes filter context in other
/// ways (ALL, REMOVEFILTERS, time intelligence…) is rejected with a clear message.
/// </summary>
public sealed class DaxTranslator
{
    readonly SemanticModel _model;
    readonly HashSet<string> _aggregated = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _tables = new(StringComparer.OrdinalIgnoreCase);
    readonly Stack<string> _inProgress = new();

    DaxTranslator(SemanticModel model) => _model = model;

    public static CompiledMeasure Translate(Measure measure, SemanticModel model)
    {
        var t = new DaxTranslator(model);
        string sql;
        try
        {
            sql = t.Measure(measure, new Scope(null, null, []));
        }
        catch (FormatException e)
        {
            throw new NotSupportedException($"Can't read the DAX of measure [{measure.Name}]: {e.Message}", e);
        }
        return new CompiledMeasure(sql, t._aggregated, t._tables);
    }

    // ------------------------------------------------------------------ Syntax tree

    abstract record Node;
    sealed record Num(double Value) : Node;
    sealed record Str(string Value) : Node;
    sealed record ColumnRef(string? Table, string Name) : Node;
    sealed record Name(string Ident) : Node;                          // table or variable
    sealed record Call(string Function, List<Node> Args) : Node;
    sealed record Binary(string Op, Node Left, Node Right) : Node;
    sealed record Unary(string Op, Node Operand) : Node;
    sealed record ListNode(List<Node> Items) : Node;
    sealed record Let(List<(string Name, Node Value)> Vars, Node Body) : Node;

    /// <summary>Where an expression is evaluated.</summary>
    /// <param name="Row">The table being iterated, or null outside a row context.</param>
    /// <param name="Filter">SQL condition from enclosing CALCULATE / FILTER calls.</param>
    sealed record Scope(string? Row, string? Filter, Dictionary<string, string> Vars)
    {
        public Scope WithFilter(string? condition) => this with { Filter = And(Filter, condition) };
    }

    static string? And(string? a, string? b) => a is null ? b : b is null ? a : $"({a}) AND ({b})";

    // ------------------------------------------------------------------ Translation

    string Measure(Measure m, Scope scope)
    {
        if (_inProgress.Contains(m.Name))
            throw new NotSupportedException($"Measure [{m.Name}] refers to itself.");
        _inProgress.Push(m.Name);
        try
        {
            var tree = new Parser(m.Expression).ParseAll();
            // A measure starts a fresh variable scope but keeps the caller's filters.
            return Emit(tree, scope with { Row = null, Vars = [] });
        }
        finally
        {
            _inProgress.Pop();
        }
    }

    string Emit(Node node, Scope s) => node switch
    {
        Num n => n.Value.ToString("R", CultureInfo.InvariantCulture),
        Str str => Sql.Literal(str.Value),
        ColumnRef c => Column(c, s),
        Name n when s.Vars.TryGetValue(n.Ident, out var v) => v,
        Name n => throw new NotSupportedException($"A table ({n.Ident}) can't be used as a value here."),
        Let let => EmitLet(let, s),
        Unary { Op: "-" } u => $"(-{Emit(u.Operand, s)})",
        Unary { Op: "NOT" } u => $"(NOT {Emit(u.Operand, s)})",
        Binary b => EmitBinary(b, s),
        Call c => EmitCall(c, s),
        ListNode => throw new NotSupportedException("A { } list can only follow IN."),
        _ => throw new NotSupportedException("Unsupported DAX expression."),
    };

    string EmitLet(Let let, Scope s)
    {
        var vars = new Dictionary<string, string>(s.Vars, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in let.Vars)
            vars[name] = Emit(value, s with { Vars = vars });
        return Emit(let.Body, s with { Vars = vars });
    }

    string Column(ColumnRef c, Scope s)
    {
        // [Name] with no table is a measure, or a column of the table being iterated.
        if (c.Table is null && _model.FindMeasure(c.Name) is { } m)
        {
            if (s.Row is not null)
                throw new NotSupportedException($"Measure [{m.Name}] is used inside a row iterator, which isn't supported.");
            return $"({Measure(m, s)})";
        }
        if (c.Table is not null && _model.FindMeasure(c.Table, c.Name) is { } tm)
            return Column(new ColumnRef(null, tm.Name), s);

        var table = c.Table ?? s.Row
            ?? throw new NotSupportedException($"Column [{c.Name}] needs a table name here.");
        if (s.Row is null)
            throw new NotSupportedException($"Column {table}[{c.Name}] must be inside an aggregation such as SUM.");
        _tables.Add(table);
        return $"{Sql.Ident(table)}.{Sql.Ident(c.Name)}";
    }

    string EmitBinary(Binary b, Scope s)
    {
        if (b.Op == "IN")
        {
            if (b.Right is not ListNode list) throw new NotSupportedException("IN must be followed by a { } list.");
            return $"({Emit(b.Left, s)} IN ({string.Join(", ", list.Items.Select(i => Emit(i, s)))}))";
        }
        var l = Emit(b.Left, s);
        var r = Emit(b.Right, s);
        return b.Op switch
        {
            // BLANK behaves like 0 in DAX addition and subtraction.
            "+" or "-" => $"(coalesce({l}, 0) {b.Op} coalesce({r}, 0))",
            "*" => $"({l} * {r})",
            "/" => $"({l} / NULLIF({r}, 0))",
            "^" => $"power({l}, {r})",
            "&" => $"(coalesce(CAST({l} AS VARCHAR), '') || coalesce(CAST({r} AS VARCHAR), ''))",
            "==" => $"({l} IS NOT DISTINCT FROM {r})",
            "=" or "<>" or "<" or ">" or "<=" or ">=" => $"({l} {b.Op} {r})",
            "&&" => $"({l} AND {r})",
            "||" => $"({l} OR {r})",
            _ => throw new NotSupportedException($"Operator {b.Op} isn't supported."),
        };
    }

    static readonly Dictionary<string, string> SimpleAggregates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SUM"] = "sum", ["AVERAGE"] = "avg", ["MIN"] = "min", ["MAX"] = "max",
        ["COUNT"] = "count", ["COUNTA"] = "count", ["DISTINCTCOUNT"] = "count DISTINCT",
        ["MEDIAN"] = "median",
    };

    static readonly Dictionary<string, string> Iterators = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SUMX"] = "sum", ["AVERAGEX"] = "avg", ["MINX"] = "min", ["MAXX"] = "max",
        ["COUNTX"] = "count", ["COUNTAX"] = "count", ["MEDIANX"] = "median",
    };

    string EmitCall(Call c, Scope s)
    {
        var f = c.Function.ToUpperInvariant();
        var a = c.Args;

        // Aggregations over a column: SUM('T'[C]) iterates T.
        if (SimpleAggregates.TryGetValue(f, out var agg) && !(f is "MIN" or "MAX" && a.Count == 2))
        {
            Arity(c, 1);
            if (a[0] is not ColumnRef col || col.Table is null)
                throw new NotSupportedException($"{f} needs a column such as Table[Column].");
            var table = Iterate(col.Table, s);
            var expr = Column(col, s with { Row = table });
            return Aggregate(agg, expr, s.Filter);
        }

        if (Iterators.TryGetValue(f, out var iter))
        {
            Arity(c, 2);
            var (table, condition) = TableExpression(a[0], s);
            var row = s with { Row = table };
            return Aggregate(iter, Emit(a[1], row), And(s.Filter, condition));
        }

        switch (f)
        {
            case "COUNTROWS":
            {
                Arity(c, 1);
                var (_, condition) = TableExpression(a[0], s);
                return Aggregate("count", "*", And(s.Filter, condition));
            }
            case "COUNTBLANK":
            {
                Arity(c, 1);
                if (a[0] is not ColumnRef { Table: not null } col) throw new NotSupportedException("COUNTBLANK needs a column.");
                var table = Iterate(col.Table, s);
                var expr = Column(col, s with { Row = table });
                return $"({Aggregate("count", "*", s.Filter)} - {Aggregate("count", expr, s.Filter)})";
            }
            case "CALCULATE":
            {
                if (a.Count == 0) throw new FormatException("CALCULATE needs an expression.");
                var inner = s;
                foreach (var filter in a.Skip(1)) inner = inner.WithFilter(FilterArgument(filter, s));
                return Emit(a[0], inner);
            }
            case "DIVIDE":
            {
                if (a.Count is < 2 or > 3) throw new FormatException("DIVIDE takes 2 or 3 arguments.");
                var num = Emit(a[0], s);
                var den = Emit(a[1], s);
                var alt = a.Count == 3 ? Emit(a[2], s) : "NULL";
                return $"(CASE WHEN {den} IS NULL OR {den} = 0 THEN {alt} ELSE {num} / {den} END)";
            }
            case "IF":
            {
                if (a.Count is < 2 or > 3) throw new FormatException("IF takes 2 or 3 arguments.");
                var otherwise = a.Count == 3 ? Emit(a[2], s) : "NULL";
                return $"(CASE WHEN {Emit(a[0], s)} THEN {Emit(a[1], s)} ELSE {otherwise} END)";
            }
            case "SWITCH":
            {
                if (a.Count < 3) throw new FormatException("SWITCH needs at least 3 arguments.");
                var subject = a[0] is Call { Function: var fn, Args.Count: 0 } && fn.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
                    ? null : Emit(a[0], s);
                var sql = new StringBuilder("(CASE");
                var i = 1;
                for (; i + 1 < a.Count; i += 2)
                {
                    var when = Emit(a[i], s);
                    sql.Append(" WHEN ").Append(subject is null ? when : $"{subject} = {when}").Append(" THEN ").Append(Emit(a[i + 1], s));
                }
                sql.Append(" ELSE ").Append(i < a.Count ? Emit(a[i], s) : "NULL").Append(" END)");
                return sql.ToString();
            }
            case "TRUE": return "TRUE";
            case "FALSE": return "FALSE";
            case "BLANK": return "NULL";
            case "NOT": Arity(c, 1); return $"(NOT {Emit(a[0], s)})";
            case "AND": Arity(c, 2); return $"({Emit(a[0], s)} AND {Emit(a[1], s)})";
            case "OR": Arity(c, 2); return $"({Emit(a[0], s)} OR {Emit(a[1], s)})";
            case "ISBLANK": Arity(c, 1); return $"({Emit(a[0], s)} IS NULL)";
            case "COALESCE": return $"coalesce({string.Join(", ", a.Select(x => Emit(x, s)))})";
            case "ABS": Arity(c, 1); return $"abs({Emit(a[0], s)})";
            case "INT": Arity(c, 1); return $"floor({Emit(a[0], s)})";
            case "SQRT": Arity(c, 1); return $"sqrt({Emit(a[0], s)})";
            case "ROUND": Arity(c, 2); return $"round({Emit(a[0], s)}, CAST({Emit(a[1], s)} AS INTEGER))";
            case "MROUND": Arity(c, 2); return $"(round({Emit(a[0], s)} / {Emit(a[1], s)}) * {Emit(a[1], s)})";
            case "MIN": return $"least({Emit(a[0], s)}, {Emit(a[1], s)})";
            case "MAX": return $"greatest({Emit(a[0], s)}, {Emit(a[1], s)})";
            case "VALUE": Arity(c, 1); return $"TRY_CAST({Emit(a[0], s)} AS DOUBLE)";
            case "LEFT": Arity(c, 2); return $"left(CAST({Emit(a[0], s)} AS VARCHAR), CAST({Emit(a[1], s)} AS INTEGER))";
            case "RIGHT": Arity(c, 2); return $"right(CAST({Emit(a[0], s)} AS VARCHAR), CAST({Emit(a[1], s)} AS INTEGER))";
            case "LEN": Arity(c, 1); return $"length(CAST({Emit(a[0], s)} AS VARCHAR))";
            case "UPPER": Arity(c, 1); return $"upper({Emit(a[0], s)})";
            case "LOWER": Arity(c, 1); return $"lower({Emit(a[0], s)})";
            case "YEAR": Arity(c, 1); return $"year({Emit(a[0], s)})";
            case "MONTH": Arity(c, 1); return $"month({Emit(a[0], s)})";
            case "DAY": Arity(c, 1); return $"day({Emit(a[0], s)})";
        }
        throw new NotSupportedException($"DAX function {c.Function} isn't supported yet.");
    }

    static void Arity(Call c, int n)
    {
        if (c.Args.Count != n) throw new FormatException($"{c.Function} takes {n} argument{(n == 1 ? "" : "s")}.");
    }

    /// <summary>Marks a table as iterated. Nested iterators must stay on the same table.</summary>
    string Iterate(string table, Scope s)
    {
        if (s.Row is not null && !string.Equals(s.Row, table, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Iterating {table} inside an iteration of {s.Row} isn't supported.");
        _aggregated.Add(table);
        return table;
    }

    static string CastDouble(string sql) => $"CAST({sql} AS DOUBLE)";

    /// <param name="function">A SQL aggregate, optionally followed by " DISTINCT".</param>
    static string Aggregate(string function, string expr, string? filter)
    {
        var parts = function.Split(' ', 2);
        var call = parts.Length == 2 ? $"{parts[0]}({parts[1]} {expr})" : $"{function}({expr})";
        if (filter is not null) call += $" FILTER (WHERE {filter})";
        return function is "min" or "max" ? call : CastDouble(call);
    }

    /// <summary>A table argument: a table name, FILTER(table, condition) or KEEPFILTERS(…).</summary>
    (string Table, string? Condition) TableExpression(Node node, Scope s)
    {
        switch (node)
        {
            case Name n when !s.Vars.ContainsKey(n.Ident):
                return (Iterate(n.Ident, s), null);
            case Call c when c.Function.Equals("KEEPFILTERS", StringComparison.OrdinalIgnoreCase) && c.Args.Count == 1:
                return TableExpression(c.Args[0], s);
            case Call c when c.Function.Equals("FILTER", StringComparison.OrdinalIgnoreCase):
            {
                Arity(c, 2);
                var (table, inner) = TableExpression(c.Args[0], s);
                var condition = Emit(c.Args[1], s with { Row = table });
                return (table, And(inner, condition));
            }
            case Call c:
                throw new NotSupportedException($"DAX function {c.Function} isn't supported yet.");
            default:
                throw new NotSupportedException("Expected a table.");
        }
    }

    /// <summary>A CALCULATE filter: a row condition, FILTER(…) or KEEPFILTERS(…), as SQL.</summary>
    string FilterArgument(Node node, Scope s)
    {
        if (node is Call c)
        {
            var f = c.Function.ToUpperInvariant();
            if (f == "KEEPFILTERS" && c.Args.Count == 1) return FilterArgument(c.Args[0], s);
            if (f == "FILTER") return TableExpression(node, s).Condition ?? "TRUE";
            if (f is "ALL" or "ALLEXCEPT" or "ALLSELECTED" or "REMOVEFILTERS" or "USERELATIONSHIP" or "CROSSFILTER"
                or "DATEADD" or "SAMEPERIODLASTYEAR" or "DATESYTD" or "DATESMTD" or "DATESQTD" or "DATESINPERIOD"
                or "DATESBETWEEN" or "PARALLELPERIOD" or "PREVIOUSYEAR" or "PREVIOUSMONTH")
                throw new NotSupportedException($"CALCULATE with {c.Function} isn't supported yet.");
        }
        // A boolean filter such as 'T'[C] = "x" reads one table's columns.
        var table = FirstTable(node) ?? throw new NotSupportedException("A CALCULATE filter must refer to a column.");
        return Emit(node, s with { Row = table });
    }

    static string? FirstTable(Node node) => node switch
    {
        ColumnRef { Table: not null } c => c.Table,
        Binary b => FirstTable(b.Left) ?? FirstTable(b.Right),
        Unary u => FirstTable(u.Operand),
        Call c => c.Args.Select(FirstTable).FirstOrDefault(t => t is not null),
        _ => null,
    };

    // ------------------------------------------------------------------ Parsing

    sealed class Parser(string text)
    {
        readonly List<(char Kind, string Text)> _tokens = Tokenize(text);
        int _pos;

        (char Kind, string Text) Peek => _pos < _tokens.Count ? _tokens[_pos] : ('$', "");

        bool IsKeyword(string word) => Peek.Kind == 'i' && Peek.Text.Equals(word, StringComparison.OrdinalIgnoreCase);

        bool Accept(string op)
        {
            if (Peek.Kind != 'o' || Peek.Text != op) return false;
            _pos++;
            return true;
        }

        void Expect(string op)
        {
            if (!Accept(op)) throw new FormatException($"Expected '{op}' near '{Peek.Text}'.");
        }

        public Node ParseAll()
        {
            var node = Expression();
            if (_pos < _tokens.Count) throw new FormatException($"Unexpected '{Peek.Text}'.");
            return node;
        }

        Node Expression()
        {
            if (!IsKeyword("VAR")) return Or();
            var vars = new List<(string, Node)>();
            while (IsKeyword("VAR"))
            {
                _pos++;
                if (Peek.Kind != 'i') throw new FormatException("Expected a variable name after VAR.");
                var name = _tokens[_pos++].Text;
                Expect("=");
                vars.Add((name, Expression()));
            }
            if (!IsKeyword("RETURN")) throw new FormatException("Expected RETURN.");
            _pos++;
            return new Let(vars, Expression());
        }

        Node Or()
        {
            var left = AndExpr();
            while (Accept("||")) left = new Binary("||", left, AndExpr());
            return left;
        }

        Node AndExpr()
        {
            var left = Comparison();
            while (Accept("&&")) left = new Binary("&&", left, Comparison());
            return left;
        }

        Node Comparison()
        {
            var left = Concat();
            while (true)
            {
                if (IsKeyword("IN")) { _pos++; left = new Binary("IN", left, Concat()); continue; }
                var op = new[] { "==", "<>", "<=", ">=", "=", "<", ">" }.FirstOrDefault(Accept);
                if (op is null) return left;
                left = new Binary(op, left, Concat());
            }
        }

        Node Concat()
        {
            var left = Additive();
            while (Accept("&")) left = new Binary("&", left, Additive());
            return left;
        }

        Node Additive()
        {
            var left = Term();
            while (true)
            {
                if (Accept("+")) left = new Binary("+", left, Term());
                else if (Accept("-")) left = new Binary("-", left, Term());
                else return left;
            }
        }

        Node Term()
        {
            var left = Power();
            while (true)
            {
                if (Accept("*")) left = new Binary("*", left, Power());
                else if (Accept("/")) left = new Binary("/", left, Power());
                else return left;
            }
        }

        Node Power()
        {
            var left = UnaryExpr();
            while (Accept("^")) left = new Binary("^", left, UnaryExpr());
            return left;
        }

        Node UnaryExpr()
        {
            if (Accept("-")) return new Unary("-", UnaryExpr());
            if (Accept("+")) return UnaryExpr();
            if (IsKeyword("NOT") && !(_pos + 1 < _tokens.Count && _tokens[_pos + 1] == ('o', "(")))
            {
                _pos++;
                return new Unary("NOT", UnaryExpr());
            }
            return Primary();
        }

        Node Primary()
        {
            var (kind, text) = Peek;
            switch (kind)
            {
                case 'n':
                    _pos++;
                    return new Num(double.Parse(text, CultureInfo.InvariantCulture));
                case 's':
                    _pos++;
                    return new Str(text);
                case 'c':
                    _pos++;
                    return new ColumnRef(null, text);
                case 't' or 'i':
                    _pos++;
                    if (Peek.Kind == 'c') return new ColumnRef(text, _tokens[_pos++].Text);
                    if (kind == 'i' && Accept("("))
                    {
                        var args = new List<Node>();
                        if (!Accept(")"))
                        {
                            do args.Add(Expression()); while (Accept(","));
                            Expect(")");
                        }
                        return new Call(text, args);
                    }
                    return new Name(text);
                case 'o' when text == "(":
                {
                    _pos++;
                    var inner = Expression();
                    Expect(")");
                    return inner;
                }
                case 'o' when text == "{":
                {
                    _pos++;
                    var items = new List<Node>();
                    if (!Accept("}"))
                    {
                        do items.Add(Expression()); while (Accept(","));
                        Expect("}");
                    }
                    return new ListNode(items);
                }
            }
            throw new FormatException(kind == '$' ? "The formula ends too early." : $"Unexpected '{text}'.");
        }

        /// <summary>Token kinds: n number, s string, c [column], t 'quoted table', i identifier, o operator.</summary>
        static List<(char, string)> Tokenize(string text)
        {
            var tokens = new List<(char, string)>();
            var i = 0;
            while (i < text.Length)
            {
                var ch = text[i];
                if (char.IsWhiteSpace(ch)) { i++; continue; }
                if (ch == '-' && At(text, i + 1, '-') || ch == '/' && At(text, i + 1, '/'))
                {
                    while (i < text.Length && text[i] != '\n') i++;
                    continue;
                }
                if (ch == '/' && At(text, i + 1, '*'))
                {
                    var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? text.Length : end + 2;
                    continue;
                }
                if (char.IsDigit(ch) || ch == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1]))
                {
                    var start = i;
                    while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.')) i++;
                    if (i < text.Length && text[i] is 'e' or 'E')
                    {
                        i++;
                        if (i < text.Length && text[i] is '+' or '-') i++;
                        while (i < text.Length && char.IsDigit(text[i])) i++;
                    }
                    tokens.Add(('n', text[start..i]));
                    continue;
                }
                if (ch is '"' or '\'' or '[')
                {
                    var close = ch == '[' ? ']' : ch;
                    var sb = new StringBuilder();
                    i++;
                    while (true)
                    {
                        if (i >= text.Length) throw new FormatException("Unclosed quote or bracket.");
                        if (text[i] == close)
                        {
                            // Doubled quote or bracket escapes itself.
                            if (At(text, i + 1, close)) { sb.Append(close); i += 2; continue; }
                            i++;
                            break;
                        }
                        sb.Append(text[i++]);
                    }
                    tokens.Add((ch switch { '"' => 's', '\'' => 't', _ => 'c' }, sb.ToString()));
                    continue;
                }
                if (char.IsLetter(ch) || ch == '_')
                {
                    var start = i;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '.')) i++;
                    tokens.Add(('i', text[start..i]));
                    continue;
                }
                var two = i + 1 < text.Length ? text.Substring(i, 2) : "";
                if (two is "&&" or "||" or "<>" or "<=" or ">=" or "==")
                {
                    tokens.Add(('o', two));
                    i += 2;
                    continue;
                }
                if ("+-*/^&=<>(),{}".Contains(ch))
                {
                    tokens.Add(('o', ch.ToString()));
                    i++;
                    continue;
                }
                throw new FormatException($"Unexpected character '{ch}'.");
            }
            return tokens;
        }

        static bool At(string text, int i, char c) => i < text.Length && text[i] == c;
    }
}
