using System.Diagnostics;
using System.Globalization;
using ClickHouse.Driver;
using ClickHouse.Driver.ADO.Parameters;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(_ => new ClickHouseClient(
    Environment.GetEnvironmentVariable("CLICKHOUSE_CONNECTION_STRING")
    ?? "Host=localhost;Port=8123;Protocol=http;Database=demo;Username=demo;Password=demo_password;UseCustomDecimals=false;Timeout=30"));
builder.Services.AddSingleton<DemoDatabase>();
builder.Services.AddSingleton<ServiceBusPipeline>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ServiceBusPipeline>());
var app = builder.Build();

app.Use(async (context, next) =>
{
    try { await next(); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception ex)
    {
        // Driver errors may include connection details. Keep credentials out of responses/logs.
        app.Logger.LogWarning("Database request failed: {FailureType}", ex.GetType().Name);
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = 503;
            await context.Response.WriteAsJsonAsync(new { error = "ClickHouse is unavailable or the query failed. Check the database container and connection settings, then retry." });
        }
    }
});
app.UseStaticFiles();
app.MapGet("/", () => Results.File(Path.Combine(app.Environment.WebRootPath, "index.xhtml"), "application/xhtml+xml; charset=utf-8"));
app.MapGet("/api/runs", async (DemoDatabase db, CancellationToken ct) =>
{
    await db.EnsureSchema(ct);
    var version = await db.Client.ExecuteScalarAsync("SELECT version()", cancellationToken: ct);
    var runs = await db.Query("""
        SELECT toString(run_id) AS run_id, count() AS events
        FROM demo.sales_events GROUP BY run_id ORDER BY run_id DESC LIMIT 50
        """, null, ct);
    var summary = await db.Query("SELECT uniqExact(run_id), count() FROM demo.sales_events", null, ct);
    return Results.Ok(new { version, runs, runCount = summary.Rows[0][0], totalEvents = summary.Rows[0][1] });
});
app.MapGet("/api/dashboard", async (Guid? runId, string? country, DemoDatabase db, CancellationToken ct) =>
{
    if (!string.IsNullOrEmpty(country) && country is not ("US" or "GB" or "DE" or "CA"))
        return Results.BadRequest(new { error = "Choose one of the available countries." });
    var p = new ClickHouseParameterCollection();
    if (runId.HasValue)
        p.Add(new ClickHouseDbParameter { ParameterName = "runId", Value = runId.Value });
    p.Add(new ClickHouseDbParameter { ParameterName = "country", Value = country ?? "" });
    // Omitting the run filter aggregates the entire local table, including runs beyond the selector's 50-item list.
    var filter = "WHERE " + (runId.HasValue ? "run_id = {runId:UUID} AND " : "")
        + "({country:String} = '' OR country = {country:String})";
    var statements = new Dictionary<string, string>
    {
        ["products"] = $"SELECT product, count() AS events, sum(quantity) AS units, sum(quantity * unit_price) AS revenue FROM demo.sales_events {filter} GROUP BY product ORDER BY revenue DESC, product",
        ["daily"] = $"SELECT toString(toDate(occurred_at)) AS day, count() AS events, sum(quantity * unit_price) AS revenue FROM demo.sales_events {filter} GROUP BY day ORDER BY day",
        ["countries"] = $"SELECT country, count() AS events, sum(quantity * unit_price) AS revenue FROM demo.sales_events {filter} GROUP BY country ORDER BY revenue DESC, country",
        ["events"] = $"SELECT event_id, toString(occurred_at) AS occurred_at_utc, product, country, quantity, unit_price, toString(run_id) AS data_run FROM demo.sales_events {filter} ORDER BY occurred_at DESC, event_id DESC, run_id DESC LIMIT 12"
    };
    var timer = Stopwatch.StartNew();
    var totals = await db.Query($"SELECT count() AS events, sum(quantity) AS units, sum(quantity * unit_price) AS revenue, uniqExact(run_id) AS runs FROM demo.sales_events {filter}", p, ct);
    var reports = new Dictionary<string, QueryResult>();
    foreach (var (key, sql) in statements) reports[key] = await db.Query(sql, p, ct);
    return Results.Ok(new { runId, country = country ?? "", totals, reports, elapsedMs = timer.ElapsedMilliseconds });
});
app.MapPost("/api/seed", async (HttpRequest request, DemoDatabase db, CancellationToken ct) =>
{
    if (request.Headers["X-Demo-Request"] != "1")
        return Results.BadRequest(new { error = "Use the dashboard to load sample data." });
    if (!await db.InsertGate.WaitAsync(0, ct))
        return Results.Conflict(new { error = "A sample load is already running. Please wait." });
    try
    {
        await db.EnsureSchema(ct);
        var runId = Guid.NewGuid();
        var rows = SampleData.CreateEvents(runId, 10_000);
        var timer = Stopwatch.StartNew();
        var inserted = await db.Client.InsertBinaryAsync("demo.sales_events",
            new[] { "run_id", "event_id", "occurred_at", "product", "country", "quantity", "unit_price" },
            rows.Select(e => new object[] { e.RunId, e.EventId, e.OccurredAt, e.Product, e.Country, e.Quantity, e.UnitPrice }),
            cancellationToken: ct);
        var parameters = new ClickHouseParameterCollection();
        parameters.Add(new ClickHouseDbParameter { ParameterName = "runId", Value = runId });
        var check = await db.Query("SELECT count(), sum(quantity * unit_price) FROM demo.sales_events WHERE run_id = {runId:UUID}", parameters, ct);
        var validated = inserted == rows.Count && Convert.ToInt64(check.Rows[0][0], CultureInfo.InvariantCulture) == rows.Count
            && Convert.ToDecimal(check.Rows[0][1], CultureInfo.InvariantCulture) == rows.Sum(e => e.Quantity * e.UnitPrice);
        if (!validated) throw new InvalidOperationException("Sample validation failed.");
        return Results.Ok(new { runId, inserted, elapsedMs = timer.ElapsedMilliseconds, validated });
    }
    finally { db.InsertGate.Release(); }
});
app.MapGet("/api/pipeline", (ServiceBusPipeline pipeline) => Results.Ok(pipeline.Snapshot()));
app.MapPost("/api/pipeline/pause", (bool paused, HttpRequest request, ServiceBusPipeline pipeline) =>
{
    if (request.Headers["X-Demo-Request"] != "1") return Results.BadRequest();
    if (!pipeline.Enabled) return Results.BadRequest(new { error = "Start the optional Service Bus Compose configuration first." });
    pipeline.SetPaused(paused);
    return Results.Ok(pipeline.Snapshot());
});
app.MapPost("/api/queue", async (HttpRequest request, ServiceBusPipeline pipeline, CancellationToken ct) =>
{
    if (request.Headers["X-Demo-Request"] != "1") return Results.BadRequest();
    if (!pipeline.Enabled) return Results.BadRequest(new { error = "Service Bus is not configured. Start Compose with compose.servicebus.yaml." });
    if (!await pipeline.PublishGate.WaitAsync(0, ct)) return Results.Conflict(new { error = "A sample run is already being queued." });
    var runId = Guid.NewGuid();
    try { return Results.Accepted($"/api/ingestion/{runId}", await pipeline.PublishRun(runId, ct)); }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        return Results.Json(new { error = "Could not finish publishing. Some events may already be queued; inspect this run before retrying.", runId }, statusCode: 503);
    }
    finally { pipeline.PublishGate.Release(); }
});
app.MapGet("/api/ingestion/{runId:guid}", async (Guid runId, DemoDatabase db, CancellationToken ct) =>
{
    var p = new ClickHouseParameterCollection();
    p.Add(new ClickHouseDbParameter { ParameterName = "runId", Value = runId });
    var totals = await db.Query("SELECT count(), sum(quantity * unit_price) FROM demo.sales_events WHERE run_id = {runId:UUID}", p, ct);
    var events = Convert.ToInt64(totals.Rows[0][0], CultureInfo.InvariantCulture);
    var revenue = Convert.ToDecimal(totals.Rows[0][1], CultureInfo.InvariantCulture);
    return Results.Ok(new { runId, storedEvents = events, revenue, expectedEvents = 10_000,
        verified = events == 10_000 && revenue == 3_192_962.50m });
});
app.Run();

internal sealed class DemoDatabase(ClickHouseClient client)
{
    public ClickHouseClient Client { get; } = client;
    public SemaphoreSlim InsertGate { get; } = new(1, 1);
    public async Task EnsureSchema(CancellationToken ct)
    {
        var schema = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "sql", "schema.sql"), ct);
        await Client.ExecuteNonQueryAsync(schema, cancellationToken: ct);
    }
    public async Task<QueryResult> Query(string sql, ClickHouseParameterCollection? parameters, CancellationToken ct)
    {
        using var reader = await Client.ExecuteReaderAsync(sql, parameters, cancellationToken: ct);
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var rows = new List<object?[]>();
        while (await reader.ReadAsync(ct))
            rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray());
        return new QueryResult(columns, rows, sql);
    }
}
internal sealed record QueryResult(string[] Columns, List<object?[]> Rows, string Sql);
