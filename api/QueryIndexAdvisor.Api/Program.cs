using System.Diagnostics;
using System.Threading.RateLimiting;
using QueryIndexAdvisor.Api;

const int MaxQueryLength = 100_000;

var builder = WebApplication.CreateBuilder(args);

// Schema metadata travels with each request; 2 MB fits a compacted 5 MB DDL file comfortably.
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 2 * 1024 * 1024);

var permitPerMinute = builder.Configuration.GetValue("RateLimit:PermitPerMinute", 60);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // ponytail: in-memory, single instance. Swap for a Redis/Valkey-backed limiter when scaling out.
    // Behind a reverse proxy set ASPNETCORE_FORWARDEDHEADERS_ENABLED=true so RemoteIpAddress is the client,
    // not the proxy. Only the rightmost X-Forwarded-For entry (added by the proxy) is used, so it can't be spoofed.
    o.AddPolicy("per-ip", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = permitPerMinute, Window = TimeSpan.FromMinutes(1) }));
});

var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? ["http://localhost:3001"];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).WithMethods("POST").WithHeaders("Content-Type")));

var app = builder.Build();
app.UseCors();
app.UseRateLimiter();

app.MapGet("/health", () => "ok");

app.MapPost("/api/analyze", (AnalyzeRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Query))
        return Results.BadRequest(new { error = "query is required." });
    if (req.Query.Length > MaxQueryLength)
        return Results.BadRequest(new { error = $"query exceeds {MaxQueryLength} characters." });
    if (req.QueryType is not ("SQL" or "LINQ") || req.Dialect is not ("SQLSERVER" or "POSTGRESQL" or "MYSQL"))
        return Results.BadRequest(new { error = "queryType must be 'SQL' or 'LINQ'; dialect must be 'SQLSERVER', 'POSTGRESQL' or 'MYSQL'." });

    var sw = Stopwatch.StartNew();
    // LINQ snippets are often partial code, so syntax errors don't block what could be analyzed;
    // the SQL analyzers return no usages when their parse fails.
    var (usages, errors, warnings) = (req.QueryType, req.Dialect) switch
    {
        ("LINQ", _) => LinqAnalyzer.Analyze(req.Query, req.Schema),
        (_, "SQLSERVER") => SqlServerAnalyzer.Analyze(req.Query, req.Schema),
        _ => AnsiSqlAnalyzer.Analyze(req.Query, req.Schema, req.Dialect),
    };
    var recommendations = IndexAdvisor.Build(usages, req.Schema, req.Dialect);
    return Results.Ok(new AnalyzeResponse(errors, warnings, recommendations, sw.ElapsedMilliseconds));
}).RequireRateLimiting("per-ip");

app.Run();

public partial class Program;
