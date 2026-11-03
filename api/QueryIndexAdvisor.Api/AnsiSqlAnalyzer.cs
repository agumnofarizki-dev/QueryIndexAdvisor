using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using SqlParser;
using SqlParser.Ast;
using SqlParser.Dialects;
using SqlParser.Tokens;

namespace QueryIndexAdvisor.Api;

/// <summary>PostgreSQL / MySQL front-end on SqlParserCS. Mirrors <see cref="SqlServerAnalyzer"/>'s rules.</summary>
public static class AnsiSqlAnalyzer
{
    public static (List<TableUsage> Usages, List<Diagnostic> Errors, List<Diagnostic> Warnings) Analyze(string sql, CompactSchemaMetadataDTO? schema, string dialect)
    {
        Dialect d = dialect == "MYSQL" ? new MySqlDialect() : new PostgreSqlDialect();
        Sequence<Statement> statements;
        try
        {
            statements = new Parser().ParseSql(sql, d);
        }
        catch (ParserException e)
        {
            return ([], [new Diagnostic("PARSE_ERROR", e.Message, (int)Math.Max(e.Line, 1), (int)Math.Max(e.Column, 1))], []);
        }
        catch (Exception e) // tokenizer errors (unterminated string, ...)
        {
            return ([], [new Diagnostic("PARSE_ERROR", e.Message, 1, 1)], []);
        }

        var walker = new Walker(schema, foldCase: dialect == "POSTGRESQL", new Locator(sql, d));
        foreach (var s in statements) walker.Statement(s);
        return (walker.Usages, [], walker.Warnings);
    }

    enum Kind { Equality, Join, Range }

    sealed class Scope : Dictionary<string, TableUsage?>
    {
        public Scope() : base(StringComparer.OrdinalIgnoreCase) { }
    }

    sealed class Walker(CompactSchemaMetadataDTO? schema, bool foldCase, Locator locator)
    {
        public readonly List<TableUsage> Usages = [];
        public readonly List<Diagnostic> Warnings = [];
        readonly List<Scope> scopes = [];
        readonly HashSet<string> cteNames = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>PostgreSQL folds unquoted identifiers to lower case; quoted ones keep their case.</summary>
        string Name(Ident i) => foldCase && i.QuoteStyle is null ? i.Value.ToLowerInvariant() : i.Value;

        public void Statement(Statement s)
        {
            switch (s)
            {
                case Statement.Select sel:
                    Query(sel.Query);
                    break;
                case Statement.Update u:
                    Dml([u.Table, .. u.From is null ? [] : new[] { u.From }], u.Selection);
                    break;
                case Statement.Delete { DeleteOperation: var op }:
                    var from = op.From?.From ?? [];
                    Dml(from, op.Selection, op.Using);
                    break;
            }
        }

        void Query(Query q)
        {
            foreach (var cte in q.With?.CteTables ?? [])
            {
                cteNames.Add(Name(cte.Alias.Name));
                Query(cte.Query);
            }
            Body(q.Body, q.OrderBy);
        }

        void Body(SetExpression body, OrderBy? orderBy)
        {
            switch (body)
            {
                case SetExpression.SelectExpression se:
                    Select(se.Select, orderBy);
                    break;
                case SetExpression.SetOperation op:
                    Body(op.Left, null);
                    Body(op.Right, null);
                    break;
                case SetExpression.QueryExpression qe:
                    Query(qe.Query);
                    break;
            }
        }

        void Select(Select s, OrderBy? orderBy)
        {
            var joins = new List<Expression>();
            var scope = new Scope();
            foreach (var t in s.From ?? []) Collect(t, scope, joins);
            scopes.Add(scope);

            foreach (var j in joins) Predicate(j);
            if (s.Selection is { } where) Predicate(where);

            // GROUP BY wins over ORDER BY: the index order feeds the aggregate.
            var groups = (s.GroupBy as GroupByExpression.Expressions)?.ColumnNames?.ToList() ?? [];
            var sort = groups.Count > 0
                ? groups.Select(g => (Expr: g, Desc: false))
                : (orderBy?.Expressions ?? []).Select(o => (Expr: o.Expression, Desc: o.Asc == false));
            foreach (var (expr, desc) in sort)
                if (Col(expr) is { } c) c.Usage.AddSort(new KeyColumn(c.Name, desc));

            foreach (var item in s.Projection ?? [])
            {
                var qualifier = item is SelectItem.QualifiedWildcard qw ? Name(qw.Name.Values.Last()) : null;
                if (item is not (SelectItem.Wildcard or SelectItem.QualifiedWildcard)) continue;
                foreach (var (alias, u) in scope)
                    if (u is not null && (qualifier is null || scope.Comparer.Equals(alias, qualifier))) u.SelectStar = true;
                Warn("SELECT_STAR", "SELECT * prevents a covering index; list only the columns you need.", item);
            }

            // Every column the query touches for a table is a covering (INCLUDE) candidate.
            IEnumerable<object> parts = orderBy is null ? [s] : [s, orderBy];
            foreach (var part in parts)
                foreach (var ids in Ast.Columns(part))
                    if (Resolve(ids.Ids, ids.Node) is { } c) c.Usage.Referenced.Add(c.Name);

            foreach (var part in parts)
                foreach (var sub in Ast.Subqueries(part)) Query(sub);
            scopes.RemoveAt(scopes.Count - 1);
        }

        // ponytail: UPDATE/DELETE get seek-key recommendations only, no covering INCLUDE.
        void Dml(IEnumerable<TableWithJoins> tables, Expression? where, TableFactor? @using = null)
        {
            var joins = new List<Expression>();
            var scope = new Scope();
            foreach (var t in tables) Collect(t, scope, joins);
            if (@using is not null) Factor(@using, scope);
            scopes.Add(scope);
            foreach (var j in joins) Predicate(j);
            if (where is not null)
            {
                Predicate(where);
                foreach (var sub in Ast.Subqueries(where)) Query(sub);
            }
            scopes.RemoveAt(scopes.Count - 1);
        }

        void Collect(TableWithJoins twj, Scope scope, List<Expression> joins)
        {
            Factor(twj.Relation, scope, joins);
            foreach (var j in twj.Joins ?? [])
            {
                Factor(j.Relation, scope, joins);
                if (j.JoinOperator?.GetType().GetProperty("JoinConstraint")?.GetValue(j.JoinOperator) is JoinConstraint.On on) joins.Add(on.Expression);
            }
        }

        void Factor(TableFactor f, Scope scope, List<Expression>? joins = null)
        {
            switch (f)
            {
                case TableFactor.Table t:
                    var parts = t.Name.Values.Select(Name).ToList();
                    var table = parts[^1];
                    var schemaName = parts.Count > 1 ? parts[^2] : null;
                    var alias = t.Alias is { } a ? Name(a.Name) : table;
                    if (schemaName is null && cteNames.Contains(table)) { scope[alias] = null; break; }
                    var usage = new TableUsage(schemaName, SchemaLookup.FindTable(schema, schemaName, table)?.TableName ?? table);
                    Usages.Add(usage);
                    scope[alias] = usage;
                    break;
                case TableFactor.NestedJoin nj:
                    Collect(nj.TableWithJoins, scope, joins ?? []);
                    break;
                case { Alias: { } other }:
                    scope[Name(other.Name)] = null; // derived table / function: not indexable here
                    break;
            }
        }

        void Predicate(Expression e)
        {
            switch (e)
            {
                case Expression.BinaryOp { Op: BinaryOperator.And } b:
                    Predicate(b.Left);
                    Predicate(b.Right);
                    break;
                case Expression.BinaryOp { Op: BinaryOperator.Or } b:
                    var cols = new List<(TableUsage Usage, string Name)>();
                    if (OrOfEqualities(b, cols) && cols.DistinctBy(c => (c.Usage, c.Name.ToUpperInvariant())).Count() == 1)
                        cols[0].Usage.AddEquality(cols[0].Name);
                    else
                        Warn("OR_PREDICATE", "OR across different columns usually prevents a single index seek; consider UNION ALL or separate indexes.", b);
                    break;
                case Expression.Nested n:
                    Predicate(n.Expression);
                    break;

                // SqlParserCS 0.6.5 precedence bugs: `NOT a = 1 AND b = 2` arrives as NOT(a = 1 AND b = 2), and
                // `x LIKE 'p' AND y` as LIKE(x, 'p' AND y). A real NOT (...) is wrapped in Nested, so these are safe to undo.
                case Expression.UnaryOp { Op: UnaryOperator.Not, Expression: Expression.BinaryOp { Op: BinaryOperator.And or BinaryOperator.Or } inner } u:
                    Predicate(inner with { Left = u with { Expression = inner.Left } });
                    break;
                case Expression.Like { Pattern: Expression.BinaryOp { Op: BinaryOperator.And or BinaryOperator.Or } p } l:
                    Predicate(p with { Left = l with { Pattern = p.Left } });
                    break;
                case Expression.ILike { Pattern: Expression.BinaryOp { Op: BinaryOperator.And or BinaryOperator.Or } p } il:
                    Predicate(p with { Left = il with { Pattern = p.Left } });
                    break;

                case Expression.BinaryOp b:
                    Comparison(b);
                    break;
                case Expression.Between { Negated: false } bt:
                    Classify(bt.Expression, Kind.Range);
                    break;
                case Expression.Like { Negated: false } l:
                    if (LeadingWildcard(l.Pattern)) Warn("LEADING_WILDCARD", "LIKE with a leading wildcard cannot seek an index; use a prefix match or full-text search.", l);
                    else Classify(l.Expression, Kind.Range);
                    break;
                case Expression.ILike { Negated: false } il:
                    Warn("NON_SARGABLE", "ILIKE cannot use a plain B-tree index; use an index on lower(column) with LIKE, or pg_trgm.", il);
                    break;
                case Expression.InList { Negated: false } i:
                    Classify(i.Expression, Kind.Equality);
                    break;
                case Expression.InSubquery { Negated: false } i:
                    Classify(i.Expression, Kind.Equality);
                    break;
                case Expression.InList or Expression.InSubquery:
                    Warn("NOT_IN", "NOT IN cannot seek an index; consider NOT EXISTS or a positive filter.", e);
                    break;
                case Expression.IsNull n:
                    Classify(n.Expression, Kind.Equality);
                    break;
                case Expression.IsNotNull n:
                    Classify(n.Expression, Kind.Range);
                    break;
                case Expression.UnaryOp { Op: UnaryOperator.Not, Expression: var inner } when Col(inner) is { } flag:
                    flag.Usage.AddEquality(flag.Name); // NOT is_deleted
                    break;
                default:
                    if (Col(e) is { } boolCol) boolCol.Usage.AddEquality(boolCol.Name); // WHERE is_active
                    break;
            }
        }

        bool OrOfEqualities(Expression e, List<(TableUsage, string)> cols)
        {
            switch (e)
            {
                case Expression.BinaryOp { Op: BinaryOperator.Or } b:
                    return OrOfEqualities(b.Left, cols) && OrOfEqualities(b.Right, cols);
                case Expression.Nested n:
                    return OrOfEqualities(n.Expression, cols);
                case Expression.BinaryOp { Op: BinaryOperator.Eq } b:
                    var l = Col(b.Left);
                    var r = Col(b.Right);
                    if ((l is null) == (r is null)) return false;
                    cols.Add((l ?? r)!.Value);
                    return true;
                case Expression.InList { Negated: false } i when Col(i.Expression) is { } c:
                    cols.Add(c);
                    return true;
                default:
                    return false;
            }
        }

        void Comparison(Expression.BinaryOp b)
        {
            Kind? kind = b.Op switch
            {
                BinaryOperator.Eq or BinaryOperator.Spaceship => Kind.Equality,
                BinaryOperator.Lt or BinaryOperator.LtEq or BinaryOperator.Gt or BinaryOperator.GtEq => Kind.Range,
                _ => null,
            };
            if (kind is null)
            {
                if (b.Op == BinaryOperator.NotEq) Warn("NOT_EQUAL", "<> / != predicates rarely use an index seek.", b);
                return;
            }

            var left = Col(b.Left);
            var right = Col(b.Right);
            if (left is null) WarnIfWrapped(b.Left);
            if (right is null) WarnIfWrapped(b.Right);

            if (left is { } l && right is { } r)
            {
                if (l.Usage == r.Usage) return;
                Add(l, kind == Kind.Equality ? Kind.Join : kind.Value);
                Add(r, kind == Kind.Equality ? Kind.Join : kind.Value);
            }
            else if ((left ?? right) is { } c && !References(left is null ? b.Left : b.Right, c.Usage))
                Add(c, kind.Value);
        }

        void Classify(Expression e, Kind kind)
        {
            if (Col(e) is { } c) Add(c, kind);
            else WarnIfWrapped(e);
        }

        static void Add((TableUsage Usage, string Name) c, Kind kind)
        {
            if (kind == Kind.Equality) c.Usage.AddEquality(c.Name);
            else if (kind == Kind.Join) c.Usage.AddJoinEquality(c.Name);
            else c.Usage.AddRange(c.Name);
        }

        static bool LeadingWildcard(Expression pattern) =>
            pattern is Expression.LiteralValue { Value: Value.SingleQuotedString s } && (s.Value.StartsWith('%') || s.Value.StartsWith('_'))
            || pattern is Expression.LiteralValue { Value: Value.DoubleQuotedString d } && (d.Value.StartsWith('%') || d.Value.StartsWith('_'));

        void WarnIfWrapped(Expression e)
        {
            foreach (var ids in Ast.Columns(e))
                if (Resolve(ids.Ids, ids.Node) is { } c)
                {
                    Warn("NON_SARGABLE", $"Column '{c.Name}' is wrapped in a function, cast or expression, so an index on it cannot be seeked. Move the computation to the other side of the comparison (or create an expression index).", e);
                    return;
                }
        }

        bool References(Expression e, TableUsage usage) => Ast.Columns(e).Any(ids => Resolve(ids.Ids, ids.Node)?.Usage == usage);

        (TableUsage Usage, string Name)? Col(Expression e)
        {
            while (e is Expression.Nested n) e = n.Expression;
            return e switch
            {
                Expression.Identifier id => Resolve([id.Ident], e),
                Expression.CompoundIdentifier ci => Resolve(ci.Idents, e),
                _ => null,
            };
        }

        (TableUsage Usage, string Name)? Resolve(IReadOnlyList<Ident> ids, object node)
        {
            if (ids.Count == 0) return null;
            var col = Name(ids[^1]);
            if (ids.Count >= 2)
            {
                var q = Name(ids[^2]);
                for (var i = scopes.Count - 1; i >= 0; i--)
                    if (scopes[i].TryGetValue(q, out var u)) return u is null ? null : (u, ColumnName(u, col));
                return null;
            }
            for (var i = scopes.Count - 1; i >= 0; i--)
            {
                var sources = scopes[i].Values.ToList();
                if (sources.Count == 1) return sources[0] is { } only ? (only, ColumnName(only, col)) : null;
                var matches = sources.Where(s => s is not null && SchemaLookup.FindColumn(SchemaLookup.FindTable(schema, s.SchemaName, s.Table), col) is not null).ToList();
                if (matches.Count == 1) return (matches[0]!, ColumnName(matches[0]!, col));
                if (matches.Count > 1 || (schema is null && sources.Count > 1))
                {
                    Warn("AMBIGUOUS_COLUMN", $"Column '{col}' is not qualified and several tables are in scope; prefix it with a table alias (or attach a schema) so it can be analyzed.", node);
                    return null;
                }
            }
            return null;
        }

        string ColumnName(TableUsage u, string col) =>
            SchemaLookup.FindColumn(SchemaLookup.FindTable(schema, u.SchemaName, u.Table), col)?.Name ?? col;

        void Warn(string code, string message, object node)
        {
            var (line, column, end) = locator.Find(node);
            if (!Warnings.Any(w => w.Code == code && w.Line == line && w.Column == column))
                Warnings.Add(new Diagnostic(code, message, line, column, end));
        }
    }

    /// <summary>
    /// SqlParserCS nodes carry no source positions, so a node is located by finding its rendered tokens
    /// in the source token stream. Each node keeps the first unclaimed occurrence of its text.
    /// </summary>
    sealed class Locator(string sql, Dialect dialect)
    {
        readonly List<Token> source = Significant(sql, dialect);
        readonly Dictionary<object, (int, int, int?)> found = new(ReferenceEqualityComparer.Instance);
        readonly HashSet<int> claimed = [];

        public (int Line, int Column, int? EndColumn) Find(object node)
        {
            if (found.TryGetValue(node, out var hit)) return hit;
            var text = node is IWriteSql w ? w.ToSql() : node.ToString() ?? "";
            List<string> want;
            try { want = Significant(text, dialect).Select(t => t.ToString()).ToList(); }
            catch { want = []; }
            int? fallback = null;
            for (var i = 0; want.Count > 0 && i + want.Count <= source.Count; i++)
            {
                if (!want.Select((t, k) => string.Equals(t, source[i + k].ToString(), StringComparison.OrdinalIgnoreCase)).All(x => x)) continue;
                fallback ??= i;
                if (claimed.Add(i)) return found[node] = Span(i, want.Count);
            }
            return found[node] = fallback is { } f ? Span(f, want.Count) : (1, 1, null);
        }

        (int, int, int?) Span(int start, int count)
        {
            var (first, last) = (source[start], source[start + count - 1]);
            var end = last.Location.Line == first.Location.Line ? (int?)(last.Location.Column + last.ToString().Length) : null;
            return ((int)first.Location.Line, (int)first.Location.Column, end);
        }

        static List<Token> Significant(string sql, Dialect d) =>
            new Tokenizer().Tokenize(sql, d).Where(t => t is not Whitespace and not EOF).ToList();
    }

    /// <summary>Reflection-based traversal: SqlParserCS has no general-purpose visitor.</summary>
    static class Ast
    {
        static readonly Assembly Assembly = typeof(Statement).Assembly;
        static readonly ConcurrentDictionary<Type, PropertyInfo[]> Props = new();

        /// <summary>Column references at this query level (subqueries are analyzed in their own scope).</summary>
        public static IEnumerable<(IReadOnlyList<Ident> Ids, Expression Node)> Columns(object root)
        {
            foreach (var n in Descendants(root))
            {
                if (n is Expression.Identifier id) yield return ([id.Ident], id);
                else if (n is Expression.CompoundIdentifier ci) yield return (ci.Idents, ci);
            }
        }

        /// <summary>Directly nested queries (scalar/EXISTS/IN subqueries, derived tables), not their descendants.</summary>
        public static IEnumerable<Query> Subqueries(object root)
        {
            var stack = new Stack<object>(Children(root).Reverse());
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (n is Query q) { yield return q; continue; }
                foreach (var c in Children(n).Reverse()) stack.Push(c);
            }
        }

        static IEnumerable<object> Descendants(object root)
        {
            var stack = new Stack<object>([root]);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (n is Query && !ReferenceEquals(n, root)) continue;
                yield return n;
                foreach (var c in Children(n).Reverse()) stack.Push(c);
            }
        }

        static IEnumerable<object> Children(object node)
        {
            if (node is IEnumerable items and not string)
            {
                foreach (var x in items) if (x is not null && x.GetType().Assembly == Assembly) yield return x;
                yield break;
            }
            foreach (var p in Props.GetOrAdd(node.GetType(), t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(p => p.GetIndexParameters().Length == 0 && p.Name != "EqualityContract" && p.PropertyType.Assembly == Assembly)
                         .ToArray()))
                if (p.GetValue(node) is { } v) yield return v;
        }
    }
}
