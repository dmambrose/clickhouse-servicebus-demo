using System.Text;
using System.Text.Json;

internal sealed class EventStream(HttpResponseMessage response, StreamReader reader) : IDisposable
{
    public static async Task<EventStream> Open(HttpClient http, CancellationToken ct)
    {
        var response = await http.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead, ct);
        try
        {
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentType?.MediaType != "text/event-stream") throw new InvalidOperationException("Not an SSE response.");
            return new EventStream(response, new StreamReader(await response.Content.ReadAsStreamAsync(ct)));
        }
        catch { response.Dispose(); throw; }
    }

    public async Task<JsonElement> Next(string expected, Func<JsonElement, bool> matches, CancellationToken ct)
    {
        var name = "message";
        var data = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(ct) ?? throw new EndOfStreamException("SSE closed unexpectedly.");
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    using var doc = JsonDocument.Parse(data.ToString());
                    if (name == expected && matches(doc.RootElement)) return doc.RootElement.Clone();
                }
                name = "message";
                data.Clear();
            }
            else if (line.StartsWith("event:")) name = line[6..].Trim();
            else if (line.StartsWith("data:")) data.AppendLine(line[5..].TrimStart());
        }
    }
    public void Dispose() { reader.Dispose(); response.Dispose(); }
}
