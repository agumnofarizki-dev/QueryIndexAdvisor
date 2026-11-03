using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using QueryIndexAdvisor.Api;

namespace QueryIndexAdvisor.Tests;

public class AdvisorTests
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static readonly Dictionary<string, Corpus> Corpora = Directory.GetFiles(".", "corpus.*.json")
        .ToDictionary(f => Path.GetFileName(f), f => JsonSerializer.Deserialize<Corpus>(File.ReadAllText(f), Json)!);
    static readonly Corpus Data = Corpora["corpus.sqlserver.json"];

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var (file, corpus) in Corpora)
            foreach (var c in corpus.Cases) data.Add(file, c.Name);
        return data;
    }

    // Benchmark corpus (PRD §1 KPI): every case must match exactly.
    [Theory]
    [MemberData(nameof(Cases))]
    public void Corpus_case(string file, string name)
    {
        var corpus = Corpora[file];
        var c = corpus.Cases.Single(x => x.Name == name);
        var schema = c.Schema is null ? null : corpus.Schemas[c.Schema];
        var dialect = corpus.Dialect ?? "SQLSERVER";
        var (usages, errors, warnings) = (corpus.QueryType, dialect) switch
        {
            ("LINQ", _) => LinqAnalyzer.Analyze(c.Query, schema),
            (_, "SQLSERVER") => SqlServerAnalyzer.Analyze(c.Query, schema),
            _ => AnsiSqlAnalyzer.Analyze(c.Query, schema, dialect),
        };
        Assert.Empty(errors);

        var recs = IndexAdvisor.Build(usages, schema, dialect);
        var actual = recs.Select(r => new
        {
            r.Table,
            Key = string.Join(", ", r.KeyColumns.Select(k => k.Name + (k.Descending ? " DESC" : ""))),
            Include = string.Join(", ", r.IncludeColumns.Order()),
            Covered = r.CoveredBy is null ? null : r.CoveredBy.FullyCovered ? "full" : "partial",
        });
        var expected = c.Expected.Select(e => new
        {
            e.Table,
            Key = string.Join(", ", e.Key),
            Include = string.Join(", ", e.Include.Order()),
            e.Covered,
        });
        Assert.Equal(expected, actual);
        for (var i = 0; i < c.Expected.Count; i++)
            if (c.Expected[i].Ddl is { } ddl) Assert.Equal(string.Join("\n", ddl), recs[i].Ddl);
        Assert.Equal((c.Warnings ?? []).Order(), warnings.Select(w => w.Code).Distinct().Order());
    }

    [Fact]
    public void Long_index_names_are_truncated_with_a_stable_hash()
    {
        var key = Enumerable.Range(1, 10).Select(i => new KeyColumn($"VeryLongColumnName{i}")).ToList();
        foreach (var (dialect, max) in new[] { ("SQLSERVER", 128), ("POSTGRESQL", 63), ("MYSQL", 64) })
        {
            var name = IndexAdvisor.IndexName("Orders", key, dialect);
            Assert.Equal(max, name.Length);
            Assert.Equal(name, IndexAdvisor.IndexName("Orders", key, dialect));
        }
        Assert.Equal("IX_Orders_CustomerId", IndexAdvisor.IndexName("Orders", [new("CustomerId")], "SQLSERVER"));
    }

    [Fact]
    public void Ddl_for_new_and_partially_covered_indexes()
    {
        var schema = Data.Schemas["shop"];
        var fresh = Recommend("SELECT Id FROM dbo.Orders WHERE CustomerId = 1 ORDER BY OrderDate DESC", null);
        Assert.Equal("CREATE NONCLUSTERED INDEX [IX_Orders_CustomerId_OrderDate]\nON [dbo].[Orders] ([CustomerId], [OrderDate] DESC)\nINCLUDE ([Id]);", fresh.Ddl);

        var widened = Recommend("SELECT Total FROM Orders WHERE Status = 'Open'", schema);
        Assert.Equal("CREATE NONCLUSTERED INDEX [IX_Orders_Status]\nON [Orders] ([Status])\nINCLUDE ([Total])\nWITH (DROP_EXISTING = ON);", widened.Ddl);

        var covered = Recommend("SELECT Total FROM Orders WHERE CustomerId = 1", schema);
        Assert.StartsWith("-- Index already covered by existing index [IX_Orders_CustomerId_OrderDate]", covered.Ddl);

        static IndexRecommendation Recommend(string sql, CompactSchemaMetadataDTO? schema)
        {
            var (usages, _, _) = SqlServerAnalyzer.Analyze(sql, schema);
            return Assert.Single(IndexAdvisor.Build(usages, schema, "SQLSERVER"));
        }
    }

    [Fact]
    public void Warnings_span_the_offending_expression()
    {
        var sql = SqlServerAnalyzer.Analyze("SELECT Id FROM Orders WHERE YEAR(OrderDate) = 2024", null).Warnings.Single();
        Assert.Equal((29, 29 + "YEAR(OrderDate)".Length), (sql.Column, sql.EndColumn));

        var linq = LinqAnalyzer.Analyze("db.Orders.Where(o => o.OrderDate.Year == 2024).Select(o => o.Id)", null).Warnings.Single();
        Assert.Equal((22, 22 + "o.OrderDate.Year".Length), (linq.Column, linq.EndColumn));

        // SqlParserCS has no positions; the locator finds each node's tokens, the second occurrence for the second node.
        var pg = AnsiSqlAnalyzer.Analyze("SELECT id FROM t WHERE lower(a) = $1\n  AND lower(a) = $2", null, "POSTGRESQL").Warnings;
        Assert.Equal([(1, 24, 32), (2, 7, 15)], pg.Select(w => (w.Line, w.Column, w.EndColumn ?? 0)));
    }

    [Fact]
    public void Parse_errors_are_reported_with_position()
    {
        var (_, errors, _) = SqlServerAnalyzer.Analyze("SELECT FROM WHERE", null);
        Assert.NotEmpty(errors);
        Assert.All(errors, e => Assert.True(e.Line >= 1));
    }

    [Fact]
    public async Task Endpoint_analyzes_validates_and_rate_limits()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("RateLimit:PermitPerMinute", "3"));
        var client = factory.CreateClient();

        var ok = await client.PostAsJsonAsync("/api/analyze", new { queryType = "SQL", dialect = "SQLSERVER", query = "SELECT Id FROM Orders WHERE CustomerId = 1" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = await ok.Content.ReadFromJsonAsync<AnalyzeResponse>(Json);
        Assert.Equal("IX_Orders_CustomerId", Assert.Single(body!.Recommendations).IndexName);

        var unsupported = await client.PostAsJsonAsync("/api/analyze", new { queryType = "SQL", dialect = "ORACLE", query = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);

        var empty = await client.PostAsJsonAsync("/api/analyze", new { queryType = "SQL", dialect = "SQLSERVER", query = "" });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var limited = await client.PostAsJsonAsync("/api/analyze", new { queryType = "SQL", dialect = "SQLSERVER", query = "SELECT 1" });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task Behind_a_proxy_each_client_ip_gets_its_own_quota_and_cannot_spoof_it()
    {
        // ASPNETCORE_FORWARDEDHEADERS_ENABLED=true on Fly.io / Render (see README).
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("RateLimit:PermitPerMinute", "1")
            .UseSetting("FORWARDEDHEADERS_ENABLED", "true"));
        var client = factory.CreateClient();

        async Task<HttpStatusCode> From(string forwardedFor)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/analyze")
            {
                Content = JsonContent.Create(new { queryType = "SQL", dialect = "SQLSERVER", query = "SELECT 1" }),
            };
            req.Headers.Add("X-Forwarded-For", forwardedFor);
            return (await client.SendAsync(req)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await From("203.0.113.1"));
        Assert.Equal(HttpStatusCode.OK, await From("203.0.113.2")); // another client, own quota
        Assert.Equal(HttpStatusCode.TooManyRequests, await From("203.0.113.1"));
        // A client-supplied entry is ignored: only the address the proxy appended (rightmost) counts.
        Assert.Equal(HttpStatusCode.TooManyRequests, await From("198.51.100.9, 203.0.113.1"));
    }

    record Corpus(Dictionary<string, CompactSchemaMetadataDTO> Schemas, List<Case> Cases, string? QueryType, string? Dialect);
    record Case(string Name, string Query, string? Schema, List<Expected> Expected, List<string>? Warnings);
    /// <param name="Ddl">Expected DDL, one array entry per line.</param>
    record Expected(string Table, List<string> Key, List<string> Include, string? Covered, List<string>? Ddl);
}
