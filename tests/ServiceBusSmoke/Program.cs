using System.Text.Json;
using Azure.Messaging.ServiceBus;

using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
var ct = timeout.Token;
using var http = new HttpClient { BaseAddress = new Uri(Environment.GetEnvironmentVariable("DEMO_BASE_URL") ?? "http://localhost:5088"), Timeout = TimeSpan.FromSeconds(60) };
http.DefaultRequestHeaders.Add("X-Demo-Request", "1");
var stage = "connecting to the pipeline";
try
{
    var status = await Get("/api/pipeline");
    if (!status.GetProperty("enabled").GetBoolean()) throw new InvalidOperationException("Start the Service Bus Compose mode first.");
    stage = "waiting for the broker and worker to become ready";
    await Until(async () => (await Get("/api/pipeline")).GetProperty("state").GetString() is "Ready" or "Processing" or "Paused");
    timeout.CancelAfter(TimeSpan.FromMinutes(5)); // Give delivery tests their own budget after cold emulator startup.
    using var stream = await EventStream.Open(http, ct);
    await stream.Next("pipeline", _ => true, ct);
    await stream.Next("dataChanged", _ => true, ct);
    stage = "pausing the worker";
    await Post("/api/pipeline/pause?paused=true");
    await Until(async () => (await Get("/api/pipeline")).GetProperty("state").GetString() == "Paused");

    stage = "queuing a sample run";
    var queued = await Post("/api/queue");
    var runId = queued.GetProperty("runId").GetGuid();
    if (queued.GetProperty("queuedMessages").GetInt32() != 10) throw new InvalidOperationException();
    var waiting = await Get($"/api/ingestion/{runId}");
    if (waiting.GetProperty("storedEvents").GetInt64() != 0) throw new InvalidOperationException("Paused worker consumed records.");
    Console.WriteLine("PASS: 10 messages carrying 10,000 events wait while the worker is paused.");

    stage = "loading queued events into ClickHouse";
    await Post("/api/pipeline/pause?paused=false");
    await Until(async () => (await Get($"/api/ingestion/{runId}")).GetProperty("verified").GetBoolean());
    var loaded = await Get($"/api/ingestion/{runId}");
    if (loaded.GetProperty("revenue").GetDecimal() != 3_192_962.50m) throw new InvalidOperationException();
    Console.WriteLine($"PASS: run {runId} has 10,000 stored events and revenue 3,192,962.50.");
    stage = "checking server-pushed ingestion events";
    await stream.Next("dataChanged", p => p.TryGetProperty("runId", out var run) && run.ValueKind == JsonValueKind.String && run.GetGuid() == runId, ct);
    Console.WriteLine("PASS: worker inserts generated an SSE data-change notification.");

    await using var client = new ServiceBusClient(Environment.GetEnvironmentVariable("SERVICEBUS_CONNECTION_STRING")
        ?? "Endpoint=sb://localhost:15673;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;");
    await using var sender = client.CreateSender("sales-events");
    status = await Get("/api/pipeline");
    var skippedBefore = status.GetProperty("skippedEvents").GetInt64();
    stage = "checking replay protection";
    // A fresh message ID bypasses broker duplicate detection and exercises the worker's event-ID check.
    var replay = new { RunId = runId, Events = new[] { new { RunId = runId, EventId = 1UL,
        OccurredAt = new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc), Product = "Monitor", Country = "US", Quantity = (ushort)1, UnitPrice = 249m } } };
    await sender.SendMessageAsync(new ServiceBusMessage(BinaryData.FromObjectAsJson(replay)) { MessageId = Guid.NewGuid().ToString(), Subject = "sales.batch.v1" }, ct);
    await Until(async () => (await Get("/api/pipeline")).GetProperty("skippedEvents").GetInt64() > skippedBefore);
    if ((await Get($"/api/ingestion/{runId}")).GetProperty("storedEvents").GetInt64() != 10_000) throw new InvalidOperationException();
    Console.WriteLine("PASS: replaying an existing event did not increase the row count.");

    stage = "checking dead-letter handling";
    var deadBefore = (await Get("/api/pipeline")).GetProperty("deadLetteredMessages").GetInt64();
    await sender.SendMessageAsync(new ServiceBusMessage("not valid sales JSON") { MessageId = Guid.NewGuid().ToString() }, ct);
    await Until(async () => (await Get("/api/pipeline")).GetProperty("deadLetteredMessages").GetInt64() > deadBefore);
    Console.WriteLine("PASS: malformed payload was dead-lettered.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL while {stage}: {ex.GetType().Name}. Check the emulator and UI logs.");
    return 1;
}
finally
{
    try { await http.PostAsync("/api/pipeline/pause?paused=false", null); }
    catch { Console.Error.WriteLine("Could not resume the worker; check its state in the dashboard."); }
}

async Task<JsonElement> Get(string path)
{
    using var response = await http.GetAsync(path, ct);
    response.EnsureSuccessStatusCode();
    using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    return doc.RootElement.Clone();
}
async Task<JsonElement> Post(string path)
{
    using var response = await http.PostAsync(path, null, ct);
    response.EnsureSuccessStatusCode();
    using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    return doc.RootElement.Clone();
}
async Task Until(Func<Task<bool>> condition)
{
    while (!await condition()) await Task.Delay(1000, ct);
}
