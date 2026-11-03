using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace QueryIndexAdvisor.Api;

/// <summary>
/// Syntax-only EF Core LINQ front-end (PRD FR-03). Snippets are never compiled, so tables and columns
/// follow EF conventions: DbSet name = table, property = column, navigation X joins on XId = Id.
/// A schema, when attached, corrects table names (plural/singular) and tells columns from navigations.
/// </summary>
public static partial class LinqAnalyzer
{
    public static (List<TableUsage> Usages, List<Diagnostic> Errors, List<Diagnostic> Warnings) Analyze(string code, CompactSchemaMetadataDTO? schema)
    {
        var tree = CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(kind: SourceCodeKind.Script));
        var errors = tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => At("PARSE_ERROR", d.GetMessage(), d.Location))
            .ToList();
        var walker = new Walker(schema);
        walker.AnalyzeRoots(tree.GetRoot(), Env.Empty);
        return (walker.Usages, errors, walker.Warnings);
    }

    static Diagnostic At(string code, string message, Location location)
    {
        var span = location.GetLineSpan();
        var (start, end) = (span.StartLinePosition, span.EndLinePosition);
        return new Diagnostic(code, message, start.Line + 1, start.Character + 1, end.Line == start.Line ? end.Character + 1 : null);
    }

    /// <summary>A lambda / range variable: an entity row of <see cref="Usage"/>, or a GroupBy group of them.</summary>
    record Binding(TableUsage Usage, bool IsGroup = false);

    sealed class Env
    {
        public static readonly Env Empty = new(ImmutableDictionary<string, Binding>.Empty);
        readonly ImmutableDictionary<string, Binding> vars;
        Env(ImmutableDictionary<string, Binding> vars) => this.vars = vars;
        public Env With(string name, Binding? b) => new(b is null ? vars.Remove(name) : vars.SetItem(name, b));
        public Binding? this[string name] => vars.GetValueOrDefault(name);
    }

    /// <summary>Result of running an operator chain: which table rows flow out of it.</summary>
    sealed class State(TableUsage usage)
    {
        public TableUsage Usage { get; } = usage;
        public TableUsage? Entity { get; set; } = usage; // non-null while full entities are returned
        public bool Projected { get; set; }              // after Select/Join/GroupBy the element is no longer the entity
        public TableUsage? Group { get; set; }           // after GroupBy
        public bool Aggregated { get; set; }             // Count/Any/Sum...: no rows materialized
        public bool ClientSide { get; set; }             // after AsEnumerable: nothing else reaches SQL
    }

    static readonly HashSet<string> LinqOps =
    [
        "Where", "Select", "SelectMany", "OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending", "GroupBy", "Join", "GroupJoin",
        "Any", "All", "Count", "LongCount", "Sum", "Min", "Max", "Average", "First", "FirstOrDefault", "Single", "SingleOrDefault",
        "Last", "LastOrDefault", "Distinct", "Take", "Skip",
    ];

    // Members that read a column's value rather than navigate to another entity.
    static readonly HashSet<string> ColumnMembers =
    [
        "Value", "HasValue", "Year", "Month", "Day", "Hour", "Minute", "Second", "Millisecond", "Date", "DayOfWeek", "DayOfYear", "TimeOfDay", "Ticks", "Length",
    ];

    // ponytail: in-memory collections (request.Items.Where(...)) look exactly like DbSets syntactically;
    // a receiver must look like a DbContext/repository to count as a table source.
    [GeneratedRegex("db|context|ctx|uow|unitofwork|repo", RegexOptions.IgnoreCase)]
    private static partial Regex ContextLike();

    sealed class Walker(CompactSchemaMetadataDTO? schema)
    {
        public readonly List<TableUsage> Usages = [];
        public readonly List<Diagnostic> Warnings = [];
        readonly Dictionary<(TableUsage, string), TableUsage> navigations = [];

        // ---- roots -------------------------------------------------------------------------

        /// <summary>Analyzes every query that starts inside <paramref name="node"/> at this lambda level.</summary>
        public void AnalyzeRoots(SyntaxNode node, Env env)
        {
            // A root's own lambdas are analyzed by the root, so the walk stops there; other lambdas
            // (app.MapGet(..., db => db.Orders...)) are just code that may contain queries.
            var roots = node.DescendantNodesAndSelf(n => !IsRoot(n, env)).Where(n => IsRoot(n, env)).ToList();
            foreach (var root in roots) Query((ExpressionSyntax)root, env);
        }

        bool IsRoot(SyntaxNode n, Env env) => n switch
        {
            QueryExpressionSyntax => true,
            InvocationExpressionSyntax inv when IsChainTop(inv) => Unroll(inv) is var (source, ops) && ops.Count > 0 && IsSource(source, ops[0], env),
            _ => false,
        };

        static bool IsChainTop(InvocationExpressionSyntax inv) =>
            !(inv.Parent is MemberAccessExpressionSyntax ma && ma.Expression == inv && ma.Parent is InvocationExpressionSyntax);

        static (ExpressionSyntax Source, List<(string Name, InvocationExpressionSyntax Call)> Ops) Unroll(ExpressionSyntax e)
        {
            var ops = new List<(string, InvocationExpressionSyntax)>();
            while (e is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax ma } inv && !IsSetCall(inv))
            {
                var name = ma.Name.Identifier.Text;
                ops.Add((name.EndsWith("Async") ? name[..^5] : name, inv));
                e = ma.Expression;
            }
            ops.Reverse();
            return (Unparen(e), ops);
        }

        static bool IsSetCall(InvocationExpressionSyntax inv) =>
            inv.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax { Identifier.Text: "Set", TypeArgumentList.Arguments.Count: 1 } }
            || inv.Expression is GenericNameSyntax { Identifier.Text: "Set" };

        bool IsSource(ExpressionSyntax source, (string Name, InvocationExpressionSyntax Call) first, Env env)
        {
            switch (source)
            {
                case QueryExpressionSyntax:
                    return true;
                case InvocationExpressionSyntax inv when IsSetCall(inv):
                    return true;
                case IdentifierNameSyntax id:
                    return env[id.Identifier.Text] is { IsGroup: true } || (env[id.Identifier.Text] is null && IsPascal(id.Identifier.Text));
                case MemberAccessExpressionSyntax ma when IsPascal(ma.Name.Identifier.Text):
                    if (ma.Expression is IdentifierNameSyntax owner && env[owner.Identifier.Text] is { IsGroup: false } b)
                        // Collection navigation (c.Orders.Any(o => ...)); string.Contains etc. on a column is not.
                        return LinqOps.Contains(first.Name) && first.Name != "Contains"
                               && !IsKnownColumn(b.Usage, ma.Name.Identifier.Text)
                               && (first.Call.ArgumentList.Arguments.Count == 0 || first.Call.ArgumentList.Arguments[0].Expression is LambdaExpressionSyntax);
                    return RootIdentifier(ma.Expression) is not { } r || (env[r] is null && (r == "this" || ContextLike().IsMatch(r)));
                default:
                    return false;
            }
        }

        // ---- chains ------------------------------------------------------------------------

        void Query(ExpressionSyntax root, Env env)
        {
            if (Run(root, env) is not { } st) return;
            if (!st.Aggregated && st.Entity is { } e)
            {
                e.SelectStar = true;
                Warn("NO_PROJECTION", "The query loads full entities (no Select), which rules out a covering index. Project only the columns you need with .Select(...).", root);
            }
        }

        State? Run(ExpressionSyntax expr, Env env)
        {
            var (source, ops) = Unroll(expr);
            State? st = source is QueryExpressionSyntax q ? QuerySyntax(q, env) : Bind(source, env) is { } u ? new State(u) : null;
            if (st is null) return null;

            foreach (var (name, call) in ops)
            {
                if (st.ClientSide) break;
                var args = call.ArgumentList.Arguments;
                switch (name)
                {
                    case "Where" or "First" or "FirstOrDefault" or "Single" or "SingleOrDefault" or "Last" or "LastOrDefault":
                        Lambda(args, 0, st, env, (body, e) => Predicate(body, e));
                        break;
                    case "Any" or "All" or "Count" or "LongCount":
                        Lambda(args, 0, st, env, (body, e) => Predicate(body, e));
                        st.Aggregated = true;
                        break;
                    case "Sum" or "Min" or "Max" or "Average":
                        Lambda(args, 0, st, env, (_, _) => { });
                        st.Aggregated = true;
                        break;
                    case "OrderBy" or "ThenBy" or "OrderByDescending" or "ThenByDescending":
                        Lambda(args, 0, st, env, (body, e) => Sort(body, name.EndsWith("Descending"), e));
                        break;
                    case "GroupBy":
                        Lambda(args, 0, st, env, (body, e) => Sort(body, false, e));
                        if (!st.Projected) st.Group = st.Usage;
                        st.Projected = true;
                        st.Entity = null;
                        break;
                    case "Select":
                        var keepsEntity = false;
                        Lambda(args, 0, st, env, (body, e) => keepsEntity = body is IdentifierNameSyntax id && e[id.Identifier.Text]?.Usage == st.Entity);
                        if (!keepsEntity) { st.Projected = true; st.Entity = null; }
                        break;
                    case "SelectMany":
                        Lambda(args, 0, st, env, (_, _) => { });
                        st.Projected = true;
                        st.Entity = null;
                        break;
                    case "Join" when args.Count >= 4:
                        Join(args, st, env);
                        break;
                    case "AsEnumerable" or "AsAsyncEnumerable" or "ToAsyncEnumerable":
                        Warn("CLIENT_EVAL", $"{name}() switches to in-memory evaluation; operators after it are not translated to SQL and cannot use an index.", call);
                        st.ClientSide = true;
                        break;
                    // Include, AsNoTracking, Take, Skip, Distinct, ToList... don't change predicates or columns.
                }
            }
            return st;
        }

        /// <summary>Binds the lambda at <paramref name="index"/> to the chain's current element and runs <paramref name="body"/> on it.</summary>
        void Lambda(SeparatedSyntaxList<ArgumentSyntax> args, int index, State st, Env env, Action<ExpressionSyntax, Env> body)
        {
            if (index >= args.Count || args[index].Expression is not LambdaExpressionSyntax lambda) return;
            var element = st.Projected ? (st.Group is { } g ? new Binding(g, IsGroup: true) : null) : new Binding(st.Usage);
            var env2 = Parameters(lambda).Take(1).Aggregate(env, (e, p) => e.With(p, element));
            RunLambdaBody(lambda, env2, body);
        }

        void RunLambdaBody(LambdaExpressionSyntax lambda, Env env, Action<ExpressionSyntax, Env> body)
        {
            var expr = lambda.ExpressionBody ?? lambda.Block?.DescendantNodes().OfType<ReturnStatementSyntax>().FirstOrDefault()?.Expression;
            if (expr is null) return;
            body(Unparen(expr), env);
            Reference(expr, env);
            AnalyzeRoots(expr, env);
        }

        void Join(SeparatedSyntaxList<ArgumentSyntax> args, State st, Env env)
        {
            var inner = Run(args[0].Expression, env);
            if (inner is null || st.Projected) { st.Projected = true; st.Entity = null; return; }
            List<(TableUsage, string)?> outerKeys = [], innerKeys = [];
            if (args[1].Expression is LambdaExpressionSyntax ok)
                RunLambdaBody(ok, env.With(Parameters(ok).First(), new Binding(st.Usage)), (b, e) => outerKeys = KeyColumns(b, e));
            if (args[2].Expression is LambdaExpressionSyntax ik)
                RunLambdaBody(ik, env.With(Parameters(ik).First(), new Binding(inner.Usage)), (b, e) => innerKeys = KeyColumns(b, e));
            JoinKeys(outerKeys, innerKeys);

            var keepsEntity = false;
            if (args[3].Expression is LambdaExpressionSyntax result && Parameters(result).ToArray() is [var o, var i, ..])
                RunLambdaBody(result, env.With(o, new Binding(st.Usage)).With(i, new Binding(inner.Usage)),
                    (b, _) => keepsEntity = b is IdentifierNameSyntax id && id.Identifier.Text == o);
            st.Projected = !keepsEntity;
            if (!keepsEntity) st.Entity = null;
        }

        State? QuerySyntax(QueryExpressionSyntax q, Env env)
        {
            if (Run(q.FromClause.Expression, env) is not { } first) return null;
            var st = new State(first.Usage);
            env = env.With(q.FromClause.Identifier.Text, new Binding(first.Usage));

            foreach (var clause in q.Body.Clauses)
            {
                switch (clause)
                {
                    case WhereClauseSyntax w:
                        Predicate(Unparen(w.Condition), env);
                        Visit(w.Condition, env);
                        break;
                    case JoinClauseSyntax j when Run(j.InExpression, env) is { } inner:
                        var innerEnv = env.With(j.Identifier.Text, new Binding(inner.Usage));
                        JoinKeys(KeyColumns(Unparen(j.LeftExpression), env), KeyColumns(Unparen(j.RightExpression), innerEnv));
                        Visit(j.LeftExpression, env);
                        Visit(j.RightExpression, innerEnv);
                        env = j.Into is null ? innerEnv : env.With(j.Into.Identifier.Text, new Binding(inner.Usage, IsGroup: true));
                        break;
                    case FromClauseSyntax f when Run(f.Expression, env) is { } more:
                        env = env.With(f.Identifier.Text, new Binding(more.Usage));
                        break;
                    case OrderByClauseSyntax ob:
                        foreach (var o in ob.Orderings)
                        {
                            Sort(Unparen(o.Expression), o.IsKind(SyntaxKind.DescendingOrdering), env);
                            Visit(o.Expression, env);
                        }
                        break;
                    case LetClauseSyntax let:
                        Visit(let.Expression, env);
                        break;
                }
            }

            st.Projected = true;
            st.Entity = null;
            switch (q.Body.SelectOrGroup)
            {
                case SelectClauseSyntax s:
                    if (Unparen(s.Expression) is IdentifierNameSyntax id && env[id.Identifier.Text] is { IsGroup: false } b) { st.Entity = b.Usage; st.Projected = false; }
                    Visit(s.Expression, env);
                    break;
                case GroupClauseSyntax g:
                    Sort(Unparen(g.ByExpression), false, env);
                    Visit(g.ByExpression, env);
                    if (Unparen(g.GroupExpression) is IdentifierNameSyntax gid && env[gid.Identifier.Text] is { IsGroup: false } gb) st.Entity = gb.Usage;
                    break;
            }
            if (q.Body.Continuation is not null) st.Entity = null; // ... into g select new { ... }
            return st;
        }

        void Visit(ExpressionSyntax e, Env env)
        {
            Reference(e, env);
            AnalyzeRoots(e, env);
        }

        /// <summary>Creates the table usage a query source refers to.</summary>
        TableUsage? Bind(ExpressionSyntax source, Env env)
        {
            switch (source)
            {
                case InvocationExpressionSyntax inv when IsSetCall(inv):
                    var type = (inv.Expression switch { MemberAccessExpressionSyntax m => m.Name, var n => (SimpleNameSyntax)n }) as GenericNameSyntax;
                    return NewUsage(ResolveTable(type!.TypeArgumentList.Arguments[0].ToString().Split('.')[^1], guessPlural: false));
                case IdentifierNameSyntax id:
                    if (env[id.Identifier.Text] is { } b) return b.IsGroup ? b.Usage : null;
                    return IsPascal(id.Identifier.Text) ? NewUsage(ResolveTable(id.Identifier.Text, guessPlural: false)) : null;
                case MemberAccessExpressionSyntax ma:
                    var name = ma.Name.Identifier.Text;
                    if (ma.Expression is IdentifierNameSyntax owner && env[owner.Identifier.Text] is { IsGroup: false } ob)
                    {
                        // Collection navigation: child.{Owner}Id = owner.Id
                        var child = NewUsage(ResolveTable(name, guessPlural: false));
                        ob.Usage.AddJoinEquality(ColumnName(ob.Usage, "Id"));
                        child.AddJoinEquality(ColumnName(child, Singular(ob.Usage.Table) + "Id"));
                        return child;
                    }
                    return NewUsage(ResolveTable(name, guessPlural: false));
                default:
                    return null;
            }
        }

        TableUsage NewUsage(string table)
        {
            var u = new TableUsage(null, table);
            Usages.Add(u);
            return u;
        }

        /// <summary>Reference navigation (o.Customer): one shared usage per owner, joined on CustomerId = Id.</summary>
        TableUsage Navigation(TableUsage owner, string nav)
        {
            if (navigations.TryGetValue((owner, nav), out var existing)) return existing;
            var target = NewUsage(ResolveTable(nav, guessPlural: true));
            owner.AddJoinEquality(ColumnName(owner, nav + "Id"));
            target.AddJoinEquality(ColumnName(target, "Id"));
            return navigations[(owner, nav)] = target;
        }

        string ResolveTable(string name, bool guessPlural)
        {
            if (schema is not null)
                foreach (var candidate in new[] { name, Plural(name), Singular(name) })
                    if (SchemaLookup.FindTable(schema, null, candidate) is { } t) return t.TableName;
            return guessPlural ? Plural(name) : name;
        }

        // ---- predicates --------------------------------------------------------------------

        void Predicate(ExpressionSyntax e, Env env)
        {
            e = Unparen(e);
            switch (e)
            {
                case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalAndExpression):
                    Predicate(b.Left, env);
                    Predicate(b.Right, env);
                    break;
                case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalOrExpression):
                    var cols = new List<(TableUsage Usage, string Name)>();
                    if (OrOfEqualities(b, env, cols) && cols.DistinctBy(c => (c.Usage, c.Name.ToUpperInvariant())).Count() == 1)
                        cols[0].Usage.AddEquality(cols[0].Name);
                    else
                        Warn("OR_PREDICATE", "|| across different columns usually prevents a single index seek; consider Concat/Union of two queries or separate indexes.", b);
                    break;
                case BinaryExpressionSyntax b:
                    Comparison(b, env);
                    break;
                case PrefixUnaryExpressionSyntax p when p.IsKind(SyntaxKind.LogicalNotExpression) && Col(p.Operand, env) is { } neg:
                    neg.Usage.AddEquality(neg.Name); // !o.IsDeleted -> IsDeleted = 0
                    break;
                case MemberAccessExpressionSyntax { Name.Identifier.Text: "HasValue" } hv when Col(hv.Expression, env) is { } nn:
                    nn.Usage.AddRange(nn.Name); // IS NOT NULL
                    break;
                case IsPatternExpressionSyntax ip when Col(ip.Expression, env) is { } pc:
                    if (ip.Pattern is ConstantPatternSyntax { Expression: LiteralExpressionSyntax lit } && lit.IsKind(SyntaxKind.NullLiteralExpression)) pc.Usage.AddEquality(pc.Name);
                    else if (ip.Pattern is UnaryPatternSyntax { Pattern: ConstantPatternSyntax { Expression: LiteralExpressionSyntax nl } } && nl.IsKind(SyntaxKind.NullLiteralExpression)) pc.Usage.AddRange(pc.Name);
                    break;
                case InvocationExpressionSyntax inv:
                    Call(inv, env);
                    break;
                default:
                    if (Col(e, env) is { } flag) flag.Usage.AddEquality(flag.Name); // bool column: o.IsActive
                    break;
            }
        }

        void Comparison(BinaryExpressionSyntax b, Env env)
        {
            var range = b.Kind() is SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression or SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression;
            var equal = b.IsKind(SyntaxKind.EqualsExpression);
            var notEqual = b.IsKind(SyntaxKind.NotEqualsExpression);
            if (!range && !equal && !notEqual) return;

            if (IsNull(b.Left) || IsNull(b.Right))
            {
                var side = IsNull(b.Left) ? b.Right : b.Left;
                if (Col(side, env) is { } nc) { if (equal) nc.Usage.AddEquality(nc.Name); else nc.Usage.AddRange(nc.Name); }
                else WarnIfWrapped(side, env);
                return;
            }
            if (notEqual)
            {
                Warn("NOT_EQUAL", "!= predicates rarely use an index seek.", b);
                return;
            }

            var left = Col(b.Left, env);
            var right = Col(b.Right, env);
            if (left is null) WarnIfWrapped(b.Left, env);
            if (right is null) WarnIfWrapped(b.Right, env);

            if (left is { } l && right is { } r)
            {
                if (l.Usage == r.Usage) return;
                if (equal) { l.Usage.AddJoinEquality(l.Name); r.Usage.AddJoinEquality(r.Name); }
                else { l.Usage.AddRange(l.Name); r.Usage.AddRange(r.Name); }
            }
            else if ((left ?? right) is { } c && !References(left is null ? b.Left : b.Right, c.Usage, env))
            {
                if (equal) c.Usage.AddEquality(c.Name);
                else c.Usage.AddRange(c.Name);
            }
        }

        void Call(InvocationExpressionSyntax inv, Env env)
        {
            if (inv.Expression is not MemberAccessExpressionSyntax ma) return;
            var args = inv.ArgumentList.Arguments;
            var receiver = Col(ma.Expression, env);
            switch (ma.Name.Identifier.Text)
            {
                case "StartsWith" when receiver is { } c:
                    c.Usage.AddRange(c.Name); // LIKE 'x%'
                    return;
                case "Contains" or "EndsWith" when receiver is not null:
                    Warn("LEADING_WILDCARD", $"string.{ma.Name.Identifier.Text} translates to LIKE '%...', which cannot seek an index; use StartsWith or full-text search.", inv);
                    return;
                case "Contains" when args.Count == 1 && Col(args[0].Expression, env) is { } item && !ReferencesAny(ma.Expression, env):
                    item.Usage.AddEquality(item.Name); // ids.Contains(o.Id) -> IN (...)
                    return;
                case "Equals" when receiver is { } c && args.Count == 1:
                    c.Usage.AddEquality(c.Name);
                    return;
                case "Like" when ma.Expression.ToString() == "EF.Functions" && args.Count >= 2:
                    if (args[1].Expression is LiteralExpressionSyntax { Token.ValueText: var pattern } && (pattern.StartsWith('%') || pattern.StartsWith('_')))
                        Warn("LEADING_WILDCARD", "LIKE with a leading wildcard cannot seek an index; use a prefix match or full-text search.", inv);
                    else if (Col(args[0].Expression, env) is { } lc) lc.Usage.AddRange(lc.Name);
                    else WarnIfWrapped(args[0].Expression, env);
                    return;
            }
            // Anything else touching a column (o.Name.ToLower().Contains(x), string.IsNullOrEmpty(o.X)...) becomes a SQL function call.
            if (ReferencesAny(ma.Expression, env) || args.Any(a => ReferencesAny(a.Expression, env))) WarnIfWrapped(inv, env);
        }

        bool OrOfEqualities(ExpressionSyntax e, Env env, List<(TableUsage, string)> cols)
        {
            e = Unparen(e);
            switch (e)
            {
                case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalOrExpression):
                    return OrOfEqualities(b.Left, env, cols) && OrOfEqualities(b.Right, env, cols);
                case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.EqualsExpression):
                    var l = Col(b.Left, env);
                    var r = Col(b.Right, env);
                    if ((l is null) == (r is null)) return false;
                    cols.Add((l ?? r)!.Value);
                    return true;
                case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Contains" } ma } inv
                    when inv.ArgumentList.Arguments.Count == 1 && Col(ma.Expression, env) is null && Col(inv.ArgumentList.Arguments[0].Expression, env) is { } c:
                    cols.Add(c);
                    return true;
                default:
                    return false;
            }
        }

        void Sort(ExpressionSyntax e, bool desc, Env env)
        {
            foreach (var k in KeyColumns(e, env))
                if (k is { } c) c.Item1.AddSort(new KeyColumn(c.Item2, desc));
        }

        /// <summary>Columns of a key selector: o.X, or new { o.A, o.B } in order (null where not a plain column).</summary>
        List<(TableUsage, string)?> KeyColumns(ExpressionSyntax e, Env env) =>
            e is AnonymousObjectCreationExpressionSyntax anon
                ? anon.Initializers.Select(i => Col(i.Expression, env)).ToList()
                : [Col(e, env)];

        static void JoinKeys(List<(TableUsage, string)?> outer, List<(TableUsage, string)?> inner)
        {
            for (var i = 0; i < Math.Min(outer.Count, inner.Count); i++)
            {
                if (outer[i] is not { } o || inner[i] is not { } n) continue;
                o.Item1.AddJoinEquality(o.Item2);
                n.Item1.AddJoinEquality(n.Item2);
            }
        }

        // ---- columns -----------------------------------------------------------------------

        /// <summary>The column <paramref name="e"/> is exactly (o.X, o.X.Value, o.Nav.Y), or null when it is wrapped or not a column.</summary>
        (TableUsage Usage, string Name)? Col(ExpressionSyntax e, Env env)
        {
            e = Unparen(e);
            if (e is MemberAccessExpressionSyntax { Name.Identifier.Text: "Value" } nullable) e = nullable.Expression;
            var root = e;
            while (root is MemberAccessExpressionSyntax m) root = m.Expression;
            return root is IdentifierNameSyntax id && ColumnRef(id, env) is { } c && c.Node == e ? (c.Usage, c.Name) : null;
        }

        /// <summary>The column a lambda parameter reference reads, and the syntax node that reads it.</summary>
        (TableUsage Usage, string Name, ExpressionSyntax Node)? ColumnRef(IdentifierNameSyntax id, Env env)
        {
            if (env[id.Identifier.Text] is not { IsGroup: false } b) return null;
            if (id.Parent is not MemberAccessExpressionSyntax first || first.Expression != id) return null;
            var a = first.Name.Identifier.Text;
            if (first.Parent is InvocationExpressionSyntax call && call.Expression == first) return null; // o.Method()
            if (first.Parent is MemberAccessExpressionSyntax second && second.Expression == first)
            {
                var member = second.Name.Identifier.Text;
                var invoked = second.Parent is InvocationExpressionSyntax inv && inv.Expression == second;
                if (invoked && LinqOps.Contains(member)) return null; // collection navigation, analyzed as its own query
                if (!invoked && !ColumnMembers.Contains(member) && !IsKnownColumn(b.Usage, a))
                {
                    var nav = Navigation(b.Usage, a);
                    return (nav, ColumnName(nav, member), second);
                }
            }
            return (b.Usage, ColumnName(b.Usage, a), first);
        }

        IEnumerable<(TableUsage Usage, string Name)> ColumnsIn(SyntaxNode e, Env env) =>
            e.DescendantNodesAndSelf(n => n is not LambdaExpressionSyntax)
                .OfType<IdentifierNameSyntax>()
                .Select(id => ColumnRef(id, env))
                .Where(c => c is not null)
                .Select(c => (c!.Value.Usage, c.Value.Name));

        /// <summary>Everything a lambda reads is a covering (INCLUDE) candidate.</summary>
        void Reference(SyntaxNode e, Env env)
        {
            foreach (var (u, name) in ColumnsIn(e, env))
                // With a schema, o.Customer in a projection is a navigation, not a column to INCLUDE.
                if (SchemaLookup.FindTable(schema, u.SchemaName, u.Table) is not { } t || SchemaLookup.FindColumn(t, name) is not null)
                    u.Referenced.Add(name);
        }

        bool References(ExpressionSyntax e, TableUsage usage, Env env) => ColumnsIn(e, env).Any(c => c.Usage == usage);
        bool ReferencesAny(ExpressionSyntax e, Env env) => ColumnsIn(e, env).Any();

        void WarnIfWrapped(ExpressionSyntax e, Env env)
        {
            if (ColumnsIn(e, env).FirstOrDefault() is { Name: not null } c)
                Warn("NON_SARGABLE", $"Column '{c.Name}' is wrapped in a method call, member access or cast (e.g. .ToLower(), .Year), which EF translates into a SQL function, so an index on it cannot be seeked. Compare the raw column instead (a date range, or a case-insensitive collation).", e);
        }

        bool IsKnownColumn(TableUsage u, string column) =>
            SchemaLookup.FindColumn(SchemaLookup.FindTable(schema, u.SchemaName, u.Table), column) is not null;

        string ColumnName(TableUsage u, string column) =>
            SchemaLookup.FindColumn(SchemaLookup.FindTable(schema, u.SchemaName, u.Table), column)?.Name ?? column;

        void Warn(string code, string message, SyntaxNode at)
        {
            var d = At(code, message, at.GetLocation());
            if (!Warnings.Any(w => w.Code == d.Code && w.Line == d.Line && w.Column == d.Column)) Warnings.Add(d);
        }
    }

    // ---- helpers ---------------------------------------------------------------------------

    static ExpressionSyntax Unparen(ExpressionSyntax e)
    {
        while (e is ParenthesizedExpressionSyntax p) e = p.Expression;
        return e;
    }

    static IEnumerable<string> Parameters(LambdaExpressionSyntax l) => l switch
    {
        SimpleLambdaExpressionSyntax s => [s.Parameter.Identifier.Text],
        ParenthesizedLambdaExpressionSyntax p => p.ParameterList.Parameters.Select(x => x.Identifier.Text),
        _ => [],
    };

    static string? RootIdentifier(ExpressionSyntax e)
    {
        while (e is MemberAccessExpressionSyntax m) e = m.Expression;
        return e switch { IdentifierNameSyntax id => id.Identifier.Text, ThisExpressionSyntax => "this", _ => null };
    }

    static bool IsNull(ExpressionSyntax e) => Unparen(e).IsKind(SyntaxKind.NullLiteralExpression);
    static bool IsPascal(string s) => s.Length > 0 && char.IsUpper(s[0]);

    // ponytail: English-only pluralization; a schema (when attached) picks the real table name.
    internal static string Plural(string s) =>
        s.EndsWith('y') && s.Length > 1 && !"aeiou".Contains(char.ToLowerInvariant(s[^2])) ? s[..^1] + "ies"
        : s.EndsWith('s') || s.EndsWith('x') || s.EndsWith("ch") || s.EndsWith("sh") ? s + "es"
        : s + "s";

    internal static string Singular(string s) =>
        s.EndsWith("ies") ? s[..^3] + "y"
        : s.EndsWith("ses") || s.EndsWith("xes") || s.EndsWith("ches") || s.EndsWith("shes") ? s[..^2]
        : s.EndsWith('s') && !s.EndsWith("ss") ? s[..^1]
        : s;
}
