using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace QueryIndexAdvisor.Api;

public static class SqlServerAnalyzer
{
    public static (List<TableUsage> Usages, List<Diagnostic> Errors, List<Diagnostic> Warnings) Analyze(string sql, CompactSchemaMetadataDTO? schema)
    {
        var fragment = new TSql170Parser(initialQuotedIdentifiers: true).Parse(new StringReader(sql), out var parseErrors);
        if (parseErrors.Count > 0)
            return ([], parseErrors.Select(e => new Diagnostic("PARSE_ERROR", e.Message, e.Line, e.Column)).ToList(), []);

        var ctes = new CteCollector();
        fragment.Accept(ctes);
        var visitor = new Visitor(schema, ctes.Names);
        fragment.Accept(visitor);
        return (visitor.Usages, [], visitor.Warnings);
    }

    enum Kind { None, Equality, Join, Range }

    // Scope maps alias -> table usage; null means a derived table / CTE we don't index.
    sealed class Scope : Dictionary<string, TableUsage?>
    {
        public Scope() : base(StringComparer.OrdinalIgnoreCase) { }
    }

    sealed class Visitor(CompactSchemaMetadataDTO? schema, HashSet<string> cteNames) : TSqlFragmentVisitor
    {
        public readonly List<TableUsage> Usages = [];
        public readonly List<Diagnostic> Warnings = [];
        readonly List<Scope> scopes = []; // innermost last

        public override void ExplicitVisit(QuerySpecification node)
        {
            var joins = new List<BooleanExpression>();
            var scope = new Scope();
            foreach (var t in node.FromClause?.TableReferences ?? []) Collect(t, scope, joins);
            scopes.Add(scope);

            foreach (var j in joins) Predicate(j);
            if (node.WhereClause?.SearchCondition is { } where) Predicate(where);

            // GROUP BY wins over ORDER BY: the index order feeds the stream aggregate.
            var groups = node.GroupByClause?.GroupingSpecifications.OfType<ExpressionGroupingSpecification>().ToList();
            var sort = groups is { Count: > 0 }
                ? groups.Select(g => (g.Expression, Desc: false))
                : node.OrderByClause?.OrderByElements.Select(o => (o.Expression, Desc: o.SortOrder == SortOrder.Descending)) ?? [];
            foreach (var (expr, desc) in sort)
                if (Col(expr) is { } c) c.Usage.AddSort(new KeyColumn(c.Name, desc));

            foreach (var star in node.SelectElements.OfType<SelectStarExpression>())
            {
                var q = star.Qualifier?.Identifiers.LastOrDefault()?.Value;
                foreach (var u in scope.Where(kv => q is null || scope.Comparer.Equals(kv.Key, q)).Select(kv => kv.Value))
                    if (u is not null) u.SelectStar = true;
                Warn("SELECT_STAR", "SELECT * prevents a covering index; list only the columns you need.", star);
            }

            // Every column the query touches for a table is a covering (INCLUDE) candidate.
            var refs = new ColumnCollector();
            node.Accept(refs);
            foreach (var cr in refs.Columns)
                if (Resolve(cr) is { } c) c.Usage.Referenced.Add(c.Name);

            base.ExplicitVisit(node);
            scopes.RemoveAt(scopes.Count - 1);
        }

        public override void ExplicitVisit(UpdateSpecification node)
        {
            DmlScope(node.Target, node.FromClause, node.WhereClause);
            base.ExplicitVisit(node);
            scopes.RemoveAt(scopes.Count - 1);
        }

        public override void ExplicitVisit(DeleteSpecification node)
        {
            DmlScope(node.Target, node.FromClause, node.WhereClause);
            base.ExplicitVisit(node);
            scopes.RemoveAt(scopes.Count - 1);
        }

        // ponytail: UPDATE/DELETE get seek-key recommendations only, no covering INCLUDE.
        void DmlScope(TableReference target, FromClause? from, WhereClause? where)
        {
            var joins = new List<BooleanExpression>();
            var scope = new Scope();
            foreach (var t in from?.TableReferences ?? []) Collect(t, scope, joins);
            if (target is NamedTableReference n && !scope.ContainsKey(n.Alias?.Value ?? n.SchemaObject.BaseIdentifier.Value))
                Collect(n, scope, joins);
            scopes.Add(scope);
            foreach (var j in joins) Predicate(j);
            if (where?.SearchCondition is { } w) Predicate(w);
        }

        void Collect(TableReference r, Scope scope, List<BooleanExpression> joins)
        {
            switch (r)
            {
                case NamedTableReference n:
                    var name = n.SchemaObject.BaseIdentifier.Value;
                    var schemaName = n.SchemaObject.SchemaIdentifier?.Value;
                    var alias = n.Alias?.Value ?? name;
                    if (schemaName is null && cteNames.Contains(name)) { scope[alias] = null; break; }
                    var known = SchemaLookup.FindTable(schema, schemaName, name);
                    var usage = new TableUsage(schemaName, known?.TableName ?? name);
                    Usages.Add(usage);
                    scope[alias] = usage;
                    break;
                case QualifiedJoin j:
                    Collect(j.FirstTableReference, scope, joins);
                    Collect(j.SecondTableReference, scope, joins);
                    joins.Add(j.SearchCondition);
                    break;
                case UnqualifiedJoin j:
                    Collect(j.FirstTableReference, scope, joins);
                    Collect(j.SecondTableReference, scope, joins);
                    break;
                case JoinParenthesisTableReference p:
                    Collect(p.Join, scope, joins);
                    break;
                case TableReferenceWithAlias { Alias: not null } other:
                    scope[other.Alias.Value] = null;
                    break;
            }
        }

        void Predicate(BooleanExpression e)
        {
            switch (e)
            {
                case BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.And } b:
                    Predicate(b.FirstExpression);
                    Predicate(b.SecondExpression);
                    break;
                case BooleanBinaryExpression b: // OR
                    var cols = new List<(TableUsage Usage, string Name)>();
                    if (OrOfEqualities(b, cols) && cols.DistinctBy(c => (c.Usage, c.Name.ToUpperInvariant())).Count() == 1)
                        cols[0].Usage.AddEquality(cols[0].Name); // a = 1 OR a = 2 behaves like IN
                    else
                        Warn("OR_PREDICATE", "OR across different columns usually prevents a single index seek; consider UNION ALL or separate indexes.", b);
                    break;
                case BooleanParenthesisExpression p:
                    Predicate(p.Expression);
                    break;
                case BooleanComparisonExpression c:
                    Comparison(c);
                    break;
                case BooleanTernaryExpression { TernaryExpressionType: BooleanTernaryExpressionType.Between } t:
                    Classify(t.FirstExpression, Kind.Range);
                    break;
                case LikePredicate { NotDefined: false } l:
                    if (l.SecondExpression is StringLiteral s && (s.Value.StartsWith('%') || s.Value.StartsWith('_')))
                        Warn("LEADING_WILDCARD", "LIKE with a leading wildcard cannot seek an index; use a prefix match or full-text search.", l);
                    else Classify(l.FirstExpression, Kind.Range);
                    break;
                case InPredicate { NotDefined: false } i:
                    Classify(i.Expression, Kind.Equality);
                    break;
                case InPredicate i:
                    Warn("NOT_IN", "NOT IN cannot seek an index; consider NOT EXISTS or a positive filter.", i);
                    break;
                case BooleanIsNullExpression n:
                    Classify(n.Expression, n.IsNot ? Kind.Range : Kind.Equality);
                    break;
            }
        }

        bool OrOfEqualities(BooleanExpression e, List<(TableUsage, string)> cols)
        {
            switch (e)
            {
                case BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.Or } b:
                    return OrOfEqualities(b.FirstExpression, cols) && OrOfEqualities(b.SecondExpression, cols);
                case BooleanParenthesisExpression p:
                    return OrOfEqualities(p.Expression, cols);
                case BooleanComparisonExpression { ComparisonType: BooleanComparisonType.Equals } c:
                    var col = Col(c.FirstExpression) ?? Col(c.SecondExpression);
                    if (col is null || (Col(c.FirstExpression) is not null && Col(c.SecondExpression) is not null)) return false;
                    cols.Add(col.Value);
                    return true;
                case InPredicate { NotDefined: false } i when Col(i.Expression) is { } ic:
                    cols.Add(ic);
                    return true;
                default:
                    return false;
            }
        }

        void Comparison(BooleanComparisonExpression c)
        {
            var kind = c.ComparisonType switch
            {
                BooleanComparisonType.Equals => Kind.Equality,
                BooleanComparisonType.GreaterThan or BooleanComparisonType.LessThan
                    or BooleanComparisonType.GreaterThanOrEqualTo or BooleanComparisonType.LessThanOrEqualTo
                    or BooleanComparisonType.NotLessThan or BooleanComparisonType.NotGreaterThan => Kind.Range,
                _ => Kind.None,
            };
            if (kind == Kind.None)
            {
                if (c.ComparisonType is BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation)
                    Warn("NOT_EQUAL", "<> / != predicates rarely use an index seek.", c);
                return;
            }

            var left = Col(c.FirstExpression);
            var right = Col(c.SecondExpression);
            if (left is null) WarnIfWrapped(c.FirstExpression);
            if (right is null) WarnIfWrapped(c.SecondExpression);

            if (left is { } l && right is { } r)
            {
                if (l.Usage == r.Usage) return; // col1 = col2 on the same row: not seekable
                // Join predicate: each side can seek on the other's value.
                Add(l, kind == Kind.Equality ? Kind.Join : kind);
                Add(r, kind == Kind.Equality ? Kind.Join : kind);
            }
            else if ((left ?? right) is { } col)
            {
                var other = left is null ? c.FirstExpression : c.SecondExpression;
                if (!References(other, col.Usage)) Add(col, kind);
            }
        }

        void Classify(ScalarExpression expr, Kind kind)
        {
            if (Col(expr) is { } c) Add(c, kind);
            else WarnIfWrapped(expr);
        }

        static void Add((TableUsage Usage, string Name) c, Kind kind)
        {
            if (kind == Kind.Equality) c.Usage.AddEquality(c.Name);
            else if (kind == Kind.Join) c.Usage.AddJoinEquality(c.Name);
            else c.Usage.AddRange(c.Name);
        }

        void WarnIfWrapped(ScalarExpression expr)
        {
            var refs = new ColumnCollector();
            expr.Accept(refs);
            var col = refs.Columns.Select(Resolve).FirstOrDefault(r => r is not null);
            if (col is { } c)
                Warn("NON_SARGABLE", $"Column '{c.Name}' is wrapped in a function or expression, so an index on it cannot be seeked. Move the computation to the other side of the comparison.", expr);
        }

        bool References(ScalarExpression expr, TableUsage usage)
        {
            var refs = new ColumnCollector();
            expr.Accept(refs);
            return refs.Columns.Any(cr => Resolve(cr)?.Usage == usage);
        }

        (TableUsage Usage, string Name)? Col(ScalarExpression expr)
        {
            while (expr is ParenthesisExpression p) expr = p.Expression;
            return expr is ColumnReferenceExpression { ColumnType: ColumnType.Regular } cr ? Resolve(cr) : null;
        }

        (TableUsage Usage, string Name)? Resolve(ColumnReferenceExpression cr)
        {
            var ids = cr.MultiPartIdentifier?.Identifiers;
            if (ids is null || ids.Count == 0) return null;
            var col = ids[^1].Value;

            if (ids.Count >= 2)
            {
                var q = ids[^2].Value;
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
                    Warn("AMBIGUOUS_COLUMN", $"Column '{col}' is not qualified and several tables are in scope; prefix it with a table alias (or attach a schema) so it can be analyzed.", cr);
                    return null;
                }
            }
            return null;
        }

        string ColumnName(TableUsage u, string col) =>
            SchemaLookup.FindColumn(SchemaLookup.FindTable(schema, u.SchemaName, u.Table), col)?.Name ?? col;

        void Warn(string code, string message, TSqlFragment at)
        {
            if (Warnings.Any(w => w.Code == code && w.Line == at.StartLine && w.Column == at.StartColumn)) return;
            var text = at.ScriptTokenStream is null ? null : string.Concat(Enumerable.Range(at.FirstTokenIndex, at.LastTokenIndex - at.FirstTokenIndex + 1).Select(i => at.ScriptTokenStream[i].Text));
            int? end = text is null || text.Contains('\n') ? null : at.StartColumn + text.Length;
            Warnings.Add(new Diagnostic(code, message, at.StartLine, at.StartColumn, end));
        }
    }

    /// <summary>Column references of one query level; subqueries are analyzed in their own scope.</summary>
    sealed class ColumnCollector : TSqlFragmentVisitor
    {
        public readonly List<ColumnReferenceExpression> Columns = [];
        public override void ExplicitVisit(ColumnReferenceExpression node) => Columns.Add(node);
        public override void ExplicitVisit(ScalarSubquery node) { }
        public override void ExplicitVisit(QueryDerivedTable node) { }
    }

    sealed class CteCollector : TSqlFragmentVisitor
    {
        public readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase);
        public override void ExplicitVisit(CommonTableExpression node)
        {
            Names.Add(node.ExpressionName.Value);
            base.ExplicitVisit(node);
        }
    }
}
