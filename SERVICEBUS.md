# Service Bus pipeline demo

This optional mode adds Microsoft's local Azure Service Bus emulator and its SQL Server dependency. Existing ClickHouse data stays in its named Docker volume.

```text
XHTML dashboard → Service Bus queue → C# worker → ClickHouse → aggregate charts
```

## Start

From the ClickHouseDemo folder:

```sh
docker compose -f compose.yaml -f compose.servicebus.yaml up -d --build ui
```

Open [the dashboard](http://localhost:5088/). The additional panel shows worker state, message counters, ingestion progress, and Pause/Resume controls. **Queue 10,000 events** sends a run through Service Bus. **Load directly** retains the original comparison path.

The first start downloads the emulator and SQL Server images and can take several minutes. Allow at least the emulator's documented 2 GB RAM and 5 GB disk, in addition to ClickHouse and the app. SQL Server 2022 Developer Edition is configured with a 2 GB memory target.

The optional Compose file sets `ACCEPT_EULA=Y` for the emulator and SQL Server Developer containers. Review the [emulator license](https://github.com/Azure/azure-service-bus-emulator-installer/blob/main/EMULATOR_EULA.txt) and [SQL Server terms](https://go.microsoft.com/fwlink/?LinkId=746388) before running this configuration. Override its local example SQL password with `SERVICEBUS_SQL_PASSWORD` in `.env` if desired.

## Try it

1. Click **Pause worker** and wait for the worker state to say Paused.
2. Click **Queue 10,000 events**. The producer sends 10 messages, each carrying 1,000 sales events.
3. Records wait in Service Bus while the worker is paused.
4. Click **Resume worker**. Progress advances as messages are written to ClickHouse.
5. The completed run is verified against 10,000 events and 3,192,962.50 USD. All runs shows the growing combined data.

The worker is a C# `BackgroundService` hosted in the ASP.NET Core app. It uses `Azure.Messaging.ServiceBus` over AMQP TCP. It receives sequentially with PeekLock and acknowledges only after an insert succeeds. Temporary failures abandon the message for retry. Malformed batches go to the dead-letter queue; broker delivery is limited to five attempts.

Before inserting, the worker checks event IDs within the same run. This prevents ordinary redeliveries from re-inserting records with this single sequential worker, including recovery after an insert succeeded but acknowledgment was interrupted. This is a demo safeguard, not a distributed exactly-once guarantee. Do not scale multiple worker replicas without a stronger idempotency design. Publishing is not a transaction across the full run: an interrupted send can leave a partial run.

Counters describe this app session, not broker queue depth. Progress and verification come from actual ClickHouse records. The dashboard refreshes when the worker stores new events, including loads initiated outside the browser. Closing the browser does not stop delivery; reopen it to see the current aggregate. Restarting the web app resets its counters and pause state.

Queued data and entities do not persist across emulator restarts. ClickHouse records already inserted remain stored. The emulator is for development and tests, not production, and is not the Azure cloud service.

## Inspect and stop

```sh
docker compose -f compose.yaml -f compose.servicebus.yaml ps
docker compose -f compose.yaml -f compose.servicebus.yaml logs servicebus
docker compose -f compose.yaml -f compose.servicebus.yaml logs ui
docker compose -f compose.yaml -f compose.servicebus.yaml down
```

Health endpoint: http://localhost:15300/health. AMQP is exposed only on localhost port 15673 (mapped to container port 5672). Override `SERVICEBUS_AMQP_PORT` and `SERVICEBUS_HTTP_PORT` in `.env` if needed. SQL Server is internal to the Docker network. Include both Compose files when starting or stopping this mode. Start `docker compose up -d ui` with the base file alone to return to the direct-only dashboard.

## Integration smoke test

With the Service Bus mode running and a .NET SDK installed:

```sh
dotnet run --project tests/ServiceBusSmoke -c Release
```

The test adds one 10,000-event run. It verifies paused queueing, delivery, exact revenue, replay protection, and malformed-message dead-lettering, then leaves the worker resumed. It uses localhost:5088 and localhost:15673 by default. Override `DEMO_BASE_URL` or `SERVICEBUS_CONNECTION_STRING` for different endpoints.

## Cloud adaptation

The SDK can target an Azure Service Bus namespace using a real connection string (or managed identity) and an Azure `sales-events` queue. Remove `UseDevelopmentEmulator=true` for Azure. This demo creates no Azure cloud resources.

Official references: [setup](https://learn.microsoft.com/en-us/azure/service-bus-messaging/test-locally-with-service-bus-emulator), [emulator limitations](https://learn.microsoft.com/en-us/azure/service-bus-messaging/overview-emulator), [.NET SDK](https://learn.microsoft.com/en-us/dotnet/api/overview/azure/messaging.servicebus-readme).
