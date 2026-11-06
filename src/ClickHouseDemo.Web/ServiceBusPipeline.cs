using System.Text.Json;
using Azure.Messaging.ServiceBus;
using ClickHouse.Driver.ADO.Parameters;

// One hosted C# worker consumes the queue sequentially. The web app also acts as the producer.
internal sealed class ServiceBusPipeline(DemoDatabase db, ILogger<ServiceBusPipeline> logger) : BackgroundService
{
    private readonly string? connectionString = Environment.GetEnvironmentVariable("SERVICEBUS_CONNECTION_STRING");
    private readonly string queueName = Environment.GetEnvironmentVariable("SERVICEBUS_QUEUE") ?? "sales-events";
    private ServiceBusClient? client;
    private ServiceBusSender? sender;
    private long published, completed, stored, skipped, deadLettered;
    private volatile bool paused;
    private volatile string state = "Disabled";
    private volatile string? lastError;
    public bool Enabled => !string.IsNullOrWhiteSpace(connectionString);
    public SemaphoreSlim PublishGate { get; } = new(1, 1);

    public object Snapshot() => new
    {
        enabled = Enabled, queue = queueName, state, paused,
        publishedMessages = Interlocked.Read(ref published),
        completedMessages = Interlocked.Read(ref completed),
        storedEvents = Interlocked.Read(ref stored),
        skippedEvents = Interlocked.Read(ref skipped),
        deadLetteredMessages = Interlocked.Read(ref deadLettered), lastError
    };
    public void SetPaused(bool value) => paused = value;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (Enabled)
        {
            client = new ServiceBusClient(connectionString!, new ServiceBusClientOptions
            {
                TransportType = ServiceBusTransportType.AmqpTcp,
                RetryOptions = new ServiceBusRetryOptions { MaxRetries = 2, TryTimeout = TimeSpan.FromSeconds(10) }
            });
            sender = client.CreateSender(queueName);
            state = "Connecting";
        }
        return base.StartAsync(cancellationToken);
    }

    public async Task<object> PublishRun(Guid runId, CancellationToken ct)
    {
        if (sender is null) throw new InvalidOperationException("Service Bus is not configured.");
        var events = SampleData.CreateEvents(runId, 10_000);
        var messages = 0;
        foreach (var chunk in events.Chunk(1000))
        {
            var batch = new SalesBatch(runId, chunk);
            var message = new ServiceBusMessage(BinaryData.FromObjectAsJson(batch))
            {
                ContentType = "application/json", Subject = "sales.batch.v1",
                MessageId = $"{runId:N}:{messages}", CorrelationId = runId.ToString()
            };
            await sender.SendMessageAsync(message, ct);
            Interlocked.Increment(ref published);
            messages++;
        }
        return new { runId, queuedEvents = events.Count, queuedMessages = messages, transport = "servicebus" };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (client is null) return;
        await using var receiver = client.CreateReceiver(queueName, new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.PeekLock, PrefetchCount = 0 });
        var schemaReady = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (paused)
                {
                    state = "Paused";
                    await Task.Delay(300, stoppingToken);
                    continue;
                }
                if (!schemaReady) { await db.EnsureSchema(stoppingToken); schemaReady = true; }
                var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2), stoppingToken);
                state = "Ready";
                lastError = null;
                if (message is null) continue;
                state = "Processing";
                try
                {
                    SalesBatch batch;
                    try
                    {
                        batch = message.Body.ToObjectFromJson<SalesBatch>() ?? throw new JsonException();
                        if (batch.RunId == Guid.Empty || batch.Events is null || batch.Events.Length is < 1 or > 1000
                            || batch.Events.Any(e => e.RunId != batch.RunId || e.EventId is < 1 or > 10_000)
                            || batch.Events.Select(e => e.EventId).Distinct().Count() != batch.Events.Length)
                            throw new JsonException("Invalid sales batch.");
                    }
                    catch (JsonException)
                    {
                        await receiver.DeadLetterMessageAsync(message, "InvalidSalesBatch", "Expected 1-1000 events for one sample run.", stoppingToken);
                        Interlocked.Increment(ref deadLettered);
                        continue;
                    }

                    // Re-delivery after an insert/ack interruption must not blindly insert the same events again.
                    // This check assumes the single sequential demo consumer, not multiple worker replicas.
                    var p = new ClickHouseParameterCollection();
                    p.Add(new ClickHouseDbParameter { ParameterName = "runId", Value = batch.RunId });
                    p.Add(new ClickHouseDbParameter { ParameterName = "first", Value = batch.Events.Min(e => e.EventId) });
                    p.Add(new ClickHouseDbParameter { ParameterName = "last", Value = batch.Events.Max(e => e.EventId) });
                    var existing = await db.Query("SELECT event_id FROM demo.sales_events WHERE run_id = {runId:UUID} AND event_id >= {first:UInt64} AND event_id <= {last:UInt64}", p, stoppingToken);
                    var ids = existing.Rows.Select(row => Convert.ToUInt64(row[0])).ToHashSet();
                    var missing = batch.Events.Where(e => !ids.Contains(e.EventId)).ToArray();
                    if (missing.Length > 0)
                    {
                        await db.Client.InsertBinaryAsync("demo.sales_events",
                            new[] { "run_id", "event_id", "occurred_at", "product", "country", "quantity", "unit_price" },
                            missing.Select(e => new object[] { e.RunId, e.EventId, e.OccurredAt, e.Product, e.Country, e.Quantity, e.UnitPrice }),
                            cancellationToken: stoppingToken);
                        Interlocked.Add(ref stored, missing.Length);
                    }
                    Interlocked.Add(ref skipped, batch.Events.Length - missing.Length);
                    // Acknowledge only after the ClickHouse insert succeeds.
                    await receiver.CompleteMessageAsync(message, stoppingToken);
                    Interlocked.Increment(ref completed);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    lastError = ex.GetType().Name;
                    logger.LogWarning("Sales batch will be retried: {FailureType}", lastError);
                    try { await receiver.AbandonMessageAsync(message, cancellationToken: stoppingToken); }
                    catch (ServiceBusException) { /* A lost lock permits broker redelivery. */ }
                    state = "Retrying";
                    await Task.Delay(1500, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                state = "Connecting";
                lastError = ex.GetType().Name;
                logger.LogWarning("Service Bus worker is reconnecting: {FailureType}", lastError);
                await Task.Delay(2000, stoppingToken);
            }
        }
        state = "Stopped";
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (sender is not null) await sender.DisposeAsync();
        if (client is not null) await client.DisposeAsync();
    }
}
internal sealed record SalesBatch(Guid RunId, SalesEvent[] Events);
