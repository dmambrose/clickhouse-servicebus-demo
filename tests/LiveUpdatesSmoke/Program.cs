using System.Text;
using System.Text.Json;

using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var ct = timeout.Token;
using var http = new HttpClient { BaseAddress = new Uri(Environment.GetEnvironmentVariable("DEMO_BASE_URL") ?? "http://localhost:5088"), Timeout = TimeSpan.FromSeconds(60) };
http.DefaultRequestHeaders.Add("X-Demo-Request", "1");
bool? originalPaused = null;
try
{
    using var first = await EventStream.Open(http, ct);
    using var second = await EventStream.Open(http, ct);
    var initial = await first.Next("pipeline", _ => true, ct);
    await second.Next("pipeline", _ => true, ct);
    await first.Next("dataChanged", _ => true, ct);
    await second.Next("dataChanged", _ => true, ct);
    Console.WriteLine("PASS: two SSE clients receive initial state and a data resync event.");

    if (initial.GetProperty("enabled").GetBoolean())
    {
        originalPaused = initial.GetProperty("paused").GetBoolean();
        var paused = !originalPaused.Value;
        await Post($"/api/pipeline/pause?paused={paused.ToString().ToLowerInvariant()}");
        await Task.WhenAll(first.Next("pipeline", p => p.GetProperty("paused").GetBoolean() == paused, ct),
            second.Next("pipeline", p => p.GetProperty("paused").GetBoolean() == paused, ct));
        Console.WriteLine("PASS: a worker-control event is broadcast to both clients.");
    }

    first.Dispose();
    var inserted = await Post("/api/seed");
    var runId = inserted.GetProperty("runId").GetGuid();
    await second.Next("dataChanged", p => p.TryGetProperty("runId", out var run) && run.ValueKind == JsonValueKind.String && run.GetGuid() == runId, ct);
    Console.WriteLine("PASS: after one client disconnects, the other receives the direct-insert event.");

    // A reconnect must provide current state/data even without any new writes after connecting.
    using var reconnect = await EventStream.Open(http, ct);
    await reconnect.Next("pipeline", _ => true, ct);
    await reconnect.Next("dataChanged", _ => true, ct);
    using var check = await http.GetAsync($"/api/ingestion/{runId}", ct);
    check.EnsureSuccessStatusCode();
    using var totals = await JsonDocument.ParseAsync(await check.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    if (!totals.RootElement.GetProperty("verified").GetBoolean()) throw new InvalidOperationException("Inserted run failed validation.");
    Console.WriteLine("PASS: reconnect resyncs state; the new run contains exactly 10,000 events and revenue 3,192,962.50.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex.GetType().Name}. Check the SSE endpoint and app logs.");
    return 1;
}
finally
{
    if (originalPaused.HasValue)
    {
        try { await http.PostAsync($"/api/pipeline/pause?paused={originalPaused.Value.ToString().ToLowerInvariant()}", null); }
        catch { Console.Error.WriteLine("Could not restore the worker's original pause state."); }
    }
}

async Task<JsonElement> Post(string path)
{
    using var response = await http.PostAsync(path, null, ct);
    response.EnsureSuccessStatusCode();
    using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    return doc.RootElement.Clone();
}
