# ClickHouse Studio: XHTML dashboard

Start the dashboard and database from this folder:

```sh
docker compose up -d --build ui
```

Open http://localhost:5088 in a modern browser. Click **Load 10,000 events** to insert and validate a new sales data run.

The dashboard includes:

- Live connection status and ClickHouse server version.
- Revenue, sales event count, units sold, and measured report time.
- A seven-day revenue chart and product revenue bars.
- An All runs view (the default), individual run selection, and country filters.
- A query lab with four reports, actual parameterized SQL, bound values, result tables, and a copy button.
- Empty, loading, and connection-error states.
- Responsive layouts for narrow screens.

The page is actual XHTML (`wwwroot/index.xhtml`) served as `application/xhtml+xml`. CSS and JavaScript are local assets with no CDN dependencies. ASP.NET Core provides the C# backend, uses the official ClickHouse.Driver client, and keeps database credentials on the server.

Each sample load creates a new run ID, preserves existing data, and checks the inserted count and exact decimal revenue. The console and web app share the same data generator. Data is fictional and represents January 1–7, 2026; it is not today's sales. Revenue is presented in USD for this sample.

The run selector shows up to 50 run IDs, sorted by ID. The default All runs view aggregates every record in the table, including runs beyond the 50 individually listed IDs. Loading new events switches back to All runs so the totals increase immediately. The **Recent events** report shows up to 12 rows ordered by event time. Report time measures the five server-side report queries together; it excludes browser/network time and is not a benchmark.

## Local C# development

Start ClickHouse first, then run:

```sh
dotnet run --project src/ClickHouseDemo.Web --urls http://localhost:5080
```

Open http://localhost:5080. To override database credentials, set `CLICKHOUSE_CONNECTION_STRING` as described in README.md. The local app does not read the Compose `.env` file.

## Stop

```sh
docker compose down
```

Data remains in the named volume. See README.md for the optional command that deletes all demo data.

## Source

- `src/ClickHouseDemo.Web/Program.cs`: C# API, query presets, sample loading, and validation.
- `src/ClickHouseDemo.Web/wwwroot/index.xhtml`: XHTML interface.
- `src/ClickHouseDemo.Web/wwwroot/styles.css`: dashboard styles.
- `src/ClickHouseDemo.Web/wwwroot/app.js`: filtering, charts, and UI interactions.
- `src/Shared/SampleData.cs`: sample generator shared with the console app.
- `Dockerfile.web`: multi-stage dashboard container build.

The UI and database HTTP ports are bound to the local computer. This is an unauthenticated local development demo with example credentials. The API exposes fixed reports rather than accepting arbitrary SQL.


To change the dashboard host port, set UI_PORT in .env before starting Compose (default 5088). The internal container port remains 8080.

## Local aggregate view

All data is stored locally in the ClickHouse Docker named volume. All runs combines revenue, event counts, units, product charts, daily charts, and country reports across that table. Country filters apply to the combined data too. Select a specific run to inspect only that batch.

Each successful load adds 10,000 events and 3,192,962.50 USD to the unfiltered aggregate. For example, two complete runs contain 20,000 events and 6,385,925.00 USD. Repeated fictional sales are counted as additional data; this is intentional for the aggregation demo.
