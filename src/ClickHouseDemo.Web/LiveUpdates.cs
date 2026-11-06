using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Features;

// Each browser gets its own bounded channel. Slow/disconnected browsers cannot block ingestion.
internal sealed class LiveUpdates
{
    private readonly ConcurrentDictionary<Guid, Channel<LiveEvent>> subscribers = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private long revision;
    public long Revision => Interlocked.Read(ref revision);

    public void DataChanged(Guid? runId = null)
    {
        var current = Interlocked.Increment(ref revision);
        Publish("dataChanged", new { dataRevision = current, runId });
    }

    public void Publish(string name, object data)
    {
        var message = new LiveEvent(name, JsonSerializer.Serialize(data, JsonOptions));
        foreach (var channel in subscribers.Values) channel.Writer.TryWrite(message);
    }

    public async Task Stream(HttpContext context, Func<object> snapshot, CancellationToken stoppingToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stoppingToken);
        var ct = cancellation.Token;
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<LiveEvent>(new BoundedChannelOptions(32)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        subscribers[id] = channel;
        try
        {
            context.Response.ContentType = "text/event-stream; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            await context.Response.WriteAsync("retry: 3000\n\n", ct);
            // Resync on every connection/reconnection rather than promising replay of an event history.
            await Write(new LiveEvent("pipeline", JsonSerializer.Serialize(snapshot(), JsonOptions)));
            await Write(new LiveEvent("dataChanged", JsonSerializer.Serialize(new { dataRevision = Revision }, JsonOptions)));
            await context.Response.Body.FlushAsync(ct);

            var available = channel.Reader.WaitToReadAsync(ct).AsTask();
            var heartbeat = Task.Delay(TimeSpan.FromSeconds(15), ct);
            while (!ct.IsCancellationRequested)
            {
                await Task.WhenAny(available, heartbeat);
                if (available.IsCompleted)
                {
                    if (!await available) break;
                    while (channel.Reader.TryRead(out var message)) await Write(message);
                    available = channel.Reader.WaitToReadAsync(ct).AsTask();
                }
                if (heartbeat.IsCompleted)
                {
                    await heartbeat;
                    await context.Response.WriteAsync(": keep-alive\n\n", ct);
                    heartbeat = Task.Delay(TimeSpan.FromSeconds(15), ct);
                }
                await context.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (IOException) { /* The browser closed its connection. */ }
        finally
        {
            subscribers.TryRemove(id, out _);
            channel.Writer.TryComplete();
            cancellation.Cancel();
        }

        Task Write(LiveEvent message) => context.Response.WriteAsync($"event: {message.Name}\ndata: {message.Json}\n\n", ct);
    }

    private sealed record LiveEvent(string Name, string Json);
}
