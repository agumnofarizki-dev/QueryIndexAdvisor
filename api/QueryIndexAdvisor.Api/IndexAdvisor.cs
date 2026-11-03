using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace QueryIndexAdvisor.Api;

/// <summary>How one table reference is used by a query. Filled by a parser front-end (SQL, LINQ).</summary>
public sealed class TableUsage(string? schemaName, string table)
{
    public string? SchemaName { get; } = schemaName;
    public string Table { get; } = table;
    public List<string> Equality { get; } = [];
    public List<string> JoinEquality { get; } = [];
    public List<string> Range { get; } = [];
    public List<KeyColumn> Sort { get; } = [];
    public HashSet<string> Referenced { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool SelectStar { get; set; }

    public void AddEquality(string col) { if (!Equality.Contains(col, StringComparer.OrdinalIgnoreCase)) Equality.Add(col); }
    public void AddJoinEquality(string col) { if (!JoinEquality.Contains(col, StringComparer.OrdinalIgnoreCase)) JoinEquality.Add(col); }
    public void AddRange(string col) { if (!Range.Contains(col, StringComparer.OrdinalIgnoreCase)) Range.Add(col); }
    public void AddSort(KeyColumn col) { if (!Sort.Any(s => Same(s.Name, col.Name))) Sort.Add(col); }

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

public static class SchemaLookup
{
    public static CompactTableSchema? FindTable(CompactSchemaMetadataDTO? schema, string? schemaName, string table)
    {
        if (schema?.Tables is null) return null;
        var full = schemaName is null ? null : $"{schemaName}.{table}";
        foreach (var (key, t) in schema.Tables)
        {
            foreach (var name in new[] { key, t.TableName })
            {
                if (name is null) continue;
                if (Eq(name, table) || (full != null && Eq(name, full)) || (full == null && name.EndsWith("." + table, StringComparison.OrdinalIgnoreCase)))
                    return t;
            }
        }
        return null;
    }

    public static ColumnMetadata? FindColumn(CompactTableSchema? table, string column) =>
        table?.Columns?.FirstOrDefault(c => Eq(c.Key, column) || Eq(c.Value.Name, column)).Value;

    static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

public static partial class IndexAdvisor
{
    static readonly StringComparer IC = StringComparer.OrdinalIgnoreCase;

    public static int MaxIdentifierLength(string dialect) => dialect switch
    {
        "POSTGRESQL" => 63,
        "MYSQL" => 64,
        _ => 128,
    };

    public static List<IndexRecommendation> Build(IEnumerable<TableUsage> usages, CompactSchemaMetadataDTO? schema, string dialect)
    {
        var drafts = new List<Draft>();
        foreach (var u in usages.Where(u => u.Equality.Count > 0 || u.JoinEquality.Count > 0 || u.Range.Count > 0))
        {
            var table = SchemaLookup.FindTable(schema, u.SchemaName, u.Table);
            // Filter equalities lead so the index also seeks when this table drives the join;
            // join columns follow so it still seeks when this table is the probed side.
            var eq = u.Equality.Concat(u.JoinEquality).Distinct(IC).ToList();
            var range = u.Range.Where(c => !eq.Contains(c, IC)).ToList();

            // Equality-first. Only the first range column can be seeked on; the rest are residual filters
            // and live in INCLUDE. With no range column, sort/group columns extend the key so the index
            // also delivers the order and the sort operator disappears.
            var key = eq.Select(c => new KeyColumn(c)).ToList();
            if (range.Count > 0) key.Add(new KeyColumn(range[0]));
            else key.AddRange(u.Sort.Where(s => !key.Any(k => IC.Equals(k.Name, s.Name))));

            // Clustered PK columns are already carried by every secondary index (SQL Server, InnoDB).
            var pk = dialect is "SQLSERVER" or "MYSQL" && table?.Columns is not null
                ? table.Columns.Values.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToHashSet(IC)
                : new HashSet<string>(IC);
            var include = (u.SelectStar ? range.Skip(1) : u.Referenced.Concat(range.Skip(1)))
                .Where(c => !key.Any(k => IC.Equals(k.Name, c)) && !pk.Contains(c))
                .Distinct(IC)
                .ToList();

            drafts.Add(new Draft(u, table, eq, range, key, include));
        }

        return Consolidate(drafts).Select(d => ToRecommendation(d, dialect)).ToList();
    }

    /// <summary>Drops proposals whose key is a left-prefix of another proposal on the same table, merging their INCLUDE.</summary>
    static List<Draft> Consolidate(List<Draft> drafts)
    {
        var kept = new List<Draft>();
        foreach (var d in drafts.OrderByDescending(d => d.Key.Count))
        {
            var host = kept.FirstOrDefault(k => SameTable(k, d) && IsLeftPrefix(Names(d.Key), Names(k.Key)));
            if (host is null) { kept.Add(d); continue; }
            foreach (var c in d.Key.Select(k => k.Name).Concat(d.Include))
                if (!host.Key.Any(k => IC.Equals(k.Name, c)) && !host.Include.Contains(c, IC)) host.Include.Add(c);
        }
        return kept.OrderBy(drafts.IndexOf).ToList();
    }

    static IndexRecommendation ToRecommendation(Draft d, string dialect)
    {
        var name = IndexName(d.Usage.Table, d.Key, dialect);
        var covered = FindCover(d, dialect);
        var ddl = covered switch
        {
            { FullyCovered: true } => $"-- Index already covered by existing index {Q(covered.IndexName, dialect)}; no new index needed.",
            not null => Widen(d, d.Table!.Indexes.First(i => i.IndexName == covered.IndexName), dialect),
            null => Create(name, false, d, d.Key, d.Include, dialect),
        };

        return new IndexRecommendation(
            Table: d.Table?.TableName ?? d.Usage.Table,
            EqualityColumns: d.Eq,
            RangeColumns: d.Range,
            SortColumns: d.Usage.Sort,
            IncludeColumns: d.Include,
            KeyColumns: d.Key,
            IndexName: name,
            Ddl: ddl,
            CoveredBy: covered);
    }

    static CoveringIndex? FindCover(Draft d, string dialect)
    {
        if (d.Table is null) return null;
        var keys = Names(d.Key);
        var candidates = (d.Table.Indexes ?? []).Select(i => (Index: i, AllColumns: false)).ToList();

        // A clustered PK (SQL Server default, InnoDB always) stores every column, so it covers any include
        // list, and its columns ride along in every secondary index.
        var pk = d.Table.Columns?.Values.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList() ?? [];
        var clusteredPk = dialect is "SQLSERVER" or "MYSQL";
        if (clusteredPk && pk.Count > 0)
            candidates.Add((new ExistingIndexMetadata("PRIMARY KEY (clustered)", true, pk, []), true));

        CoveringIndex? partial = null;
        foreach (var (index, allColumns) in candidates)
        {
            var indexKeys = index.KeyColumns ?? [];
            // Every key of a unique index pinned by a filter equality means at most one row: nothing to gain.
            var uniqueLookup = index.IsUnique && indexKeys.Count > 0 && indexKeys.All(k => d.Usage.Equality.Contains(k, IC));
            if (!uniqueLookup && !IsLeftPrefix(keys, indexKeys)) continue;
            var stored = indexKeys.Concat(index.IncludeColumns ?? []).Concat(clusteredPk ? pk : []).ToHashSet(IC);
            var full = allColumns || uniqueLookup || d.Include.All(stored.Contains);
            var cover = new CoveringIndex(index.IndexName, indexKeys, index.IncludeColumns ?? [], full);
            if (full) return cover;
            // Constraint-backed indexes (the PK; any UNIQUE on PostgreSQL) can't just be dropped and rebuilt wider.
            var isPk = pk.Count > 0 && indexKeys.Count == pk.Count && indexKeys.All(k => pk.Contains(k, IC));
            if (!allColumns && !isPk && !(dialect == "POSTGRESQL" && index.IsUnique)) partial ??= cover;
        }
        return partial;
    }

    public static bool IsLeftPrefix(IReadOnlyList<string> prefix, IReadOnlyList<string> keys) =>
        prefix.Count <= keys.Count && prefix.Select((c, i) => IC.Equals(c, keys[i])).All(x => x);

    public static string IndexName(string table, IEnumerable<KeyColumn> key, string dialect)
    {
        var raw = Regex.Replace($"IX_{table}_{string.Join("_", key.Select(k => k.Name))}", @"[^\w]", "_");
        if (dialect == "POSTGRESQL") raw = raw.ToLowerInvariant(); // unquoted, idiomatic PG name
        var max = MaxIdentifierLength(dialect);
        if (raw.Length <= max) return raw;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..8];
        if (dialect == "POSTGRESQL") hash = hash.ToLowerInvariant();
        return $"{raw[..(max - 9)]}_{hash}";
    }

    static string Create(string name, bool unique, Draft d, List<KeyColumn> key, List<string> include, string dialect, string? sqlServerWith = null)
    {
        var target = TableRef(d.Usage, dialect);
        var u = unique ? "UNIQUE " : "";
        var keys = string.Join(", ", key.Select(k => Q(k.Name, dialect) + (k.Descending ? " DESC" : "")));
        switch (dialect)
        {
            case "MYSQL":
                var (payload, notes) = MySqlPayload(key, include, d.Table);
                return $"{notes}CREATE {u}INDEX {Q(name, dialect)}\nON {target} ({keys}{string.Concat(payload.Select(c => ", " + Q(c, dialect)))});";
            case "POSTGRESQL":
                return $"CREATE {u}INDEX {Q(name, dialect)}\nON {target} ({keys})" + (include.Count > 0 ? $"\nINCLUDE ({string.Join(", ", include.Select(c => Q(c, dialect)))})" : "") + ";";
            default:
                return $"CREATE {u}NONCLUSTERED INDEX {Q(name, dialect)}\nON {target} ({keys})"
                       + (include.Count > 0 ? $"\nINCLUDE ({string.Join(", ", include.Select(c => Q(c, dialect)))})" : "")
                       + (sqlServerWith is null ? "" : "\n" + sqlServerWith) + ";";
        }
    }

    /// <summary>The key already exists: widen that index instead of adding an overlapping one.</summary>
    static string Widen(Draft d, ExistingIndexMetadata existing, string dialect)
    {
        var keys = existing.KeyColumns.Select(c => new KeyColumn(c)).ToList();
        var include = (existing.IncludeColumns ?? []).Concat(d.Include).Where(c => !existing.KeyColumns.Contains(c, IC)).Distinct(IC).ToList();
        switch (dialect)
        {
            case "POSTGRESQL":
                var indexRef = d.Usage.SchemaName is null ? Q(existing.IndexName, dialect) : $"{Q(d.Usage.SchemaName, dialect)}.{Q(existing.IndexName, dialect)}";
                return $"BEGIN;\nDROP INDEX {indexRef};\n{Create(existing.IndexName, existing.IsUnique, d, keys, include, dialect)}\nCOMMIT;";
            case "MYSQL":
                var (payload, notes) = MySqlPayload(keys, include, d.Table);
                var cols = string.Join(", ", keys.Select(k => Q(k.Name, dialect)).Concat(payload.Select(c => Q(c, dialect))));
                return $"{notes}ALTER TABLE {TableRef(d.Usage, dialect)}\n  DROP INDEX {Q(existing.IndexName, dialect)},\n  ADD {(existing.IsUnique ? "UNIQUE " : "")}INDEX {Q(existing.IndexName, dialect)} ({cols});";
            default:
                return Create(existing.IndexName, existing.IsUnique, d, keys, include, dialect, "WITH (DROP_EXISTING = ON)");
        }
    }

    /// <summary>MySQL has no INCLUDE: payload columns are appended to the key so the index still covers the query.</summary>
    static (List<string> Payload, string Notes) MySqlPayload(List<KeyColumn> key, List<string> include, CompactTableSchema? table)
    {
        var lob = include.Where(c => SchemaLookup.FindColumn(table, c)?.DataType is { } t && LobType().IsMatch(t)).ToList();
        var payload = include.Except(lob, IC).ToList();
        var notes = new StringBuilder();
        if (key.Count + payload.Count > MySqlMaxIndexColumns)
        {
            notes.Append($"-- Payload columns not added: an InnoDB index allows at most {MySqlMaxIndexColumns} columns.\n");
            payload = [];
        }
        if (payload.Count > 0) notes.Append("-- MySQL has no INCLUDE; payload columns are appended to the key so the index covers the query.\n");
        if (lob.Count > 0) notes.Append($"-- Not covered: {string.Join(", ", lob)} (TEXT/BLOB/JSON columns need a prefix length to be indexed).\n");
        return (payload, notes.ToString());
    }

    const int MySqlMaxIndexColumns = 16;

    [GeneratedRegex("text|blob|json|geometry", RegexOptions.IgnoreCase)]
    private static partial Regex LobType();

    static string TableRef(TableUsage u, string dialect) =>
        u.SchemaName is null ? Q(u.Table, dialect) : $"{Q(u.SchemaName, dialect)}.{Q(u.Table, dialect)}";

    static string Q(string id, string dialect) => dialect switch
    {
        // Unquoted PG identifiers fold to lower case, so anything else must be quoted to keep its spelling.
        "POSTGRESQL" => PgPlainIdentifier().IsMatch(id) && !PgReserved.Contains(id) ? id : $"\"{id.Replace("\"", "\"\"")}\"",
        "MYSQL" => $"`{id.Replace("`", "``")}`",
        _ => $"[{id.Replace("]", "]]")}]",
    };

    [GeneratedRegex("^[a-z_][a-z0-9_$]*$")]
    private static partial Regex PgPlainIdentifier();

    static readonly HashSet<string> PgReserved =
    [
        "all", "analyse", "analyze", "and", "any", "array", "as", "asc", "asymmetric", "both", "case", "cast", "check", "collate", "column",
        "constraint", "create", "current_date", "current_role", "current_time", "current_timestamp", "current_user", "default", "deferrable",
        "desc", "distinct", "do", "else", "end", "except", "false", "fetch", "for", "foreign", "from", "grant", "group", "having", "in",
        "initially", "intersect", "into", "lateral", "leading", "limit", "localtime", "localtimestamp", "not", "null", "offset", "on", "only",
        "or", "order", "placing", "primary", "references", "returning", "select", "session_user", "some", "symmetric", "table", "then", "to",
        "trailing", "true", "union", "unique", "user", "using", "variadic", "when", "where", "window", "with",
    ];
    static List<string> Names(List<KeyColumn> key) => key.Select(k => k.Name).ToList();
    static bool SameTable(Draft a, Draft b) =>
        IC.Equals(a.Usage.Table, b.Usage.Table) && IC.Equals(a.Usage.SchemaName ?? "", b.Usage.SchemaName ?? "");

    sealed record Draft(TableUsage Usage, CompactTableSchema? Table, List<string> Eq, List<string> Range, List<KeyColumn> Key, List<string> Include);
}
