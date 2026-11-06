# ClickHouse + C# Docker demo

A .NET 8 console application and XHTML analytics dashboard using the official `ClickHouse.Driver` 1.5.0 HTTP client and `clickhouse/clickhouse-server:25.8`.

The optional Azure Service Bus emulator adds a complete local messaging pipeline:

```text
XHTML dashboard → Service Bus queue → C# worker → ClickHouse → aggregate charts
```

To run the complete example, install Docker with Linux containers and run:

```sh
docker compose -f compose.yaml -f compose.servicebus.yaml up -d --build ui
```

Open [the dashboard](http://localhost:5088/) and click **Queue 10,000 events**. Use **Pause worker** and **Resume worker** to see messages wait in the queue before loading into ClickHouse. The All runs view combines stored sales across all runs.

See [SERVICEBUS.md](SERVICEBUS.md) for emulator requirements and license terms, delivery behavior, and the repeatable integration smoke test. For a ClickHouse-only experience, use the startup below.

## Interactive XHTML dashboard

A browser dashboard is included. From this folder, run:

```sh
docker compose up -d --build ui
```

Open [ClickHouse Studio](http://localhost:5088). Click **Load 10,000 events**, watch the All runs totals grow, explore the charts, filter countries, and inspect the SQL. See [DASHBOARD.md](DASHBOARD.md) for details.

## Optional Service Bus queue

Send sample sales through a local Azure Service Bus emulator and C# worker before they reach ClickHouse. See [SERVICEBUS.md](SERVICEBUS.md) for startup, pause/resume, progress, and delivery details.

## Run entirely in Docker

Requires Docker with Linux containers and the Docker Compose plugin. No local .NET SDK is needed for this option. Run these commands from this folder:

```sh
docker compose up -d --wait clickhouse
docker compose run --build --rm demo
```

The first run downloads the images and builds the C# app. A successful run exits with code 0 and ends with `PASS`. Any failed validation or connection returns code 1.

## Run C# locally

Requires the .NET 8 SDK or a newer compatible SDK, plus Docker for the database:

```sh
docker compose up -d --wait clickhouse
dotnet run --project src/ClickHouseDemo -c Release
```

Open `ClickHouseDemo.sln` in Visual Studio to explore/debug the app. Start ClickHouse before running it.

## What the demo does

1. Connects over HTTP and prints the server version.
2. Creates `demo.sales_events` with the MergeTree engine, monthly partitions, UTC timestamps, low-cardinality strings, and decimal prices.
3. Generates 10,000 repeatable sales events and inserts them using `InsertBinaryAsync`.
4. Prints revenue by product and day.
5. Uses typed query parameters to filter by run ID and country.
6. Verifies the inserted row count, exact decimal revenue, and US event count against the generated C# data.

Each run uses a new UUID. The console reports only that run; the dashboard defaults to combining all runs. Previous data stays available. MergeTree does not enforce uniqueness, and this demo does not implement exactly-once delivery. The data volume grows by 10,000 events per completed run. A failed or canceled run may leave partial data under its run ID.

`sql/schema.sql` is copied into the application output and executed by the app. The database and user are created by the official image's startup environment variables. The application has a two-minute overall cancellation limit.

## Connection settings

| Setting | Local app | App in Docker |
| --- | --- | --- |
| Host | localhost | clickhouse |
| HTTP port | 8123 | 8123 |
| Database / user | demo / demo | demo / demo |
| Password | demo_password | demo_password |

Compose exposes HTTP only on `127.0.0.1`. The native TCP port is not published because the C# client uses HTTP. These credentials and the plain HTTP configuration are for a local demo.

Optional: copy `.env.example` to `.env` and edit `CLICKHOUSE_PASSWORD` before starting Compose. Compose passes it to both containers. A local `dotnet run` does not read `.env`; set `CLICKHOUSE_CONNECTION_STRING` in your shell if you change the password. For PowerShell:

```powershell
$env:CLICKHOUSE_CONNECTION_STRING = 'Host=localhost;Port=8123;Protocol=http;Database=demo;Username=demo;Password=your_password;UseCustomDecimals=false;Timeout=30'
dotnet run --project src/ClickHouseDemo -c Release
```

`UseCustomDecimals=false` maps the small demo Decimal columns to .NET `decimal`. The driver and server versions are deliberately specified; `25.8` is a moving branch tag, not an immutable image digest.

## Inspect the data

Connect interactively; enter the configured password when prompted:

```sh
docker compose exec clickhouse clickhouse-client --user demo --password --database demo
```

Then run:

```sql
SELECT run_id, count() AS events, sum(quantity * unit_price) AS revenue
FROM sales_events
GROUP BY run_id
ORDER BY run_id;
```

## Stop and reset

```sh
docker compose down
```

This stops and removes the containers while preserving the named data volume. To deliberately delete all demo data as well:

```sh
docker compose down --volumes
```

## Troubleshooting

- Ensure Docker Desktop is running in Linux container mode.
- If startup fails, use `docker compose ps` and `docker compose logs clickhouse`.
- If port 8123 is already occupied, change the host side of the port mapping and the local app connection string. The container app still uses port 8123.
- If you change credentials, recreate the ClickHouse container and use the same credentials in the local connection string.
- Insert times are observations from a tiny sample, not a benchmark.

## Files and references

- `compose.yaml`: database, readiness check, persistent volume, and demo service.
- `Dockerfile`: multi-stage .NET build and non-root runtime.
- `src/ClickHouseDemo/Program.cs`: data generation, bulk insert, queries, and verification.
- `sql/schema.sql`: table definition.

Official references: [Docker image](https://hub.docker.com/r/clickhouse/clickhouse-server/), [C# HTTP client documentation](https://clickhouse.com/docs/integrations/language-clients/csharp/http), [client source](https://github.com/ClickHouse/clickhouse-cs).
