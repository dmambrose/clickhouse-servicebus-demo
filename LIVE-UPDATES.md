# Live dashboard updates with SSE

The XHTML page opens one persistent HTTP connection to `/api/events` using the browser's built-in `EventSource` API. The C# app pushes notifications when the worker state, message counters, or stored data change. There is no periodic dashboard polling loop.

```text
Direct load / Service Bus worker → LiveUpdates → SSE connection → XHTML JavaScript → report refresh
```

Button actions still use HTTP POST requests. SSE carries server-to-browser notifications. When data changes, JavaScript fetches the current reports from the existing C# endpoints, so SQL filtering, decimal calculations, and run validation remain on the server. Bursts of notifications are coalesced while a report request is running; the final change is retained.

## Events

| Event | Payload | Browser action |
| --- | --- | --- |
| `pipeline` | Worker state, pause state, session counters, data revision | Update the pipeline controls and counters; refresh if its data revision changed. |
| `dataChanged` | Data revision and optional run UUID | Refresh the selected scope and any pending run's ingestion progress. |

Every connection sends an initial pipeline snapshot and a data resync notification. `EventSource` reconnects automatically after a dropped connection, using the server's three-second retry hint. Reconnection resyncs current state; it does not replay a stored event history. The header badge shows **Live · SSE** or **SSE reconnecting…**.

The server sends a comment heartbeat every 15 seconds and flushes event frames. Heartbeats do not query ClickHouse. Response buffering is disabled for the stream. Each browser gets an independent bounded channel; a slow client cannot block ingestion. Data revisions in status snapshots ensure a later status update can signal data changes even when older buffered notifications are coalesced.

This event broadcaster belongs to the single web-app process. Inserts performed by this web app and its hosted worker send events. Changes made directly in ClickHouse or by the separate console app require the dashboard's Refresh button. Multiple web-app replicas would need a shared notification mechanism.

## Source

- `src/ClickHouseDemo.Web/LiveUpdates.cs`: subscription channels, SSE formatting, heartbeats, cancellation, and reconnect snapshots.
- `src/ClickHouseDemo.Web/ServiceBusPipeline.cs`: publishes status and successful-insert notifications.
- `src/ClickHouseDemo.Web/Program.cs`: maps `/api/events` and publishes direct-load notifications.
- `src/ClickHouseDemo.Web/wwwroot/app.js`: `EventSource` handlers and coalesced report refreshes.

## Verify

Start the app with either Compose mode, then run:

```sh
dotnet run --project tests/LiveUpdatesSmoke -c Release
```

This adds one direct 10,000-event run. It checks two simultaneous SSE clients, worker-control broadcasts when Service Bus is enabled, continued delivery after one client disconnects, and reconnect resync. It restores the worker's original pause state.

The existing Service Bus smoke test also verifies that worker inserts emit SSE notifications:

```sh
dotnet run --project tests/ServiceBusSmoke -c Release
```

For a manual stream view:

```sh
curl -N http://localhost:5088/api/events
```

Protocol reference: [MDN: Using server-sent events](https://developer.mozilla.org/en-US/docs/Web/API/Server-sent_events/Using_server-sent_events).
