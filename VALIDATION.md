# Validation

## Service Bus integration

- C# build and Docker build passed with Azure.Messaging.ServiceBus 7.21.0.
- The optional Compose stack runs ClickHouse, the web app/worker, Microsoft's Service Bus emulator, and SQL Server 2022 Developer Edition.
- Broker health endpoint returned HTTP 200; SQL readiness check is healthy.
- Final integration smoke test passed: 10 messages carrying 10,000 events remained queued while paused, then loaded with exact revenue 3,192,962.50 USD.
- Sending an existing event under a fresh message ID did not increase its run's row count.
- A malformed payload was dead-lettered successfully.
- Browser test of the final queue button passed: progress reached 10,000 stored events, 10 messages handled, and the combined view grew from 430,000 to 440,000 events (revenue 140,490,350.00 USD).
- XHTML parsing and JavaScript syntax checks passed. The pipeline panel, status, counters, and growing aggregate were inspected in a working fresh browser tab.
- The worker is left resumed. Queue counters reset when the app restarts; the emulator's queued messages do not persist across emulator restarts.

The repeatable smoke test is in tests/ServiceBusSmoke. See SERVICEBUS.md for usage and local emulator limitations.

Validated on November 6, 2026 with .NET SDK 9.0.313 targeting .NET 8, Docker 29.7.2 (Linux containers), and Docker Compose v5.3.1.

- Release build: passed, 0 warnings, 0 errors.
- Compose configuration: passed.
- ClickHouse 25.8.33.6 startup and authenticated health check: passed.
- Local C# integration run: passed.
- 10,000 inserted events; exact revenue 3,192,962.50; US event count 2,475.
- Product revenue and seven daily totals printed successfully.

The timings depend on the machine and are not a performance benchmark.

- All-Docker multi-stage build and container integration run: passed with exit code 0 and the same verified totals.
- A second run retained the earlier run and reported only its own 10,000 events.

## XHTML dashboard validation

- Both C# projects compile in Release with 0 warnings and 0 errors.
- Dashboard Docker build and startup passed.
- Root page returns HTTP 200 with application/xhtml+xml and parses as XML.
- JavaScript syntax check passed.
- Dashboard API: 10,000 events, 30,066 units, revenue 3,192,962.50; US filter: 2,475 events.
- Reports return 4 products, 7 days, and 12 recent events.
- Invalid country values and sample-load requests without the expected header return HTTP 400.
- Browser checked at narrow/mobile and desktop widths.
- Browser sample-load button inserted and validated a new 10,000-event run, selected it automatically, and reset the country filter.
- Browser country filter, query tabs, and Copy SQL button passed.
- Console app regression run after extracting the shared sample generator passed.

The dashboard is available at http://localhost:5088 while its Docker services are running.

- Empty data-run query returns zero totals and empty reports.
- Console Docker rebuild and integration run after the shared-generator change passed (exit code 0).

## All runs aggregation update

- Dashboard defaults to All runs; per-run views and country filters remain available.
- Loading a verified batch increased the aggregate from 250,000 to 260,000 events and revenue from 79,824,062.50 to 83,017,025.00 USD.
- Product and daily revenue reports matched the combined total.
- Final container/API checks passed for all runs, one run, and an all-runs US filter. A later check with 30 complete runs returned 300,000 events and 95,788,875.00 USD.
- Individual run still returned 10,000 events and 3,192,962.50 USD after correcting the recent-events run column alias.
- XHTML parses as XML; JavaScript syntax check passed; deployed assets include the All runs behavior.
- Browser preview reload could not complete because the in-app browser reported a connection reset; updated visual interactions were not reverified in this iteration. The local HTTP page and API were reachable and tested separately.

## Server-Sent Events update

- Release solution build passed with 0 warnings and 0 errors.
- JavaScript syntax, XHTML XML parsing, and whitespace checks passed.
- Dashboard uses EventSource and named SSE events instead of periodic browser polling.
- LiveUpdatesSmoke passed: two subscribers, pause broadcasts, independent disconnects, direct inserts, reconnect snapshots, and exact run totals.
- ServiceBusSmoke passed: pause/queue/resume, worker SSE notification, 10,000 verified rows, replay protection, and poison-message dead lettering.
- Final Docker UI image built and started successfully without restarting the database or broker.
- Browser automatically reconnected after the UI container restart without a page reload.
- Browser queued a new run and displayed verified progress and aggregate updates from 730,000 to 740,000 events; total revenue reached 236,279,225.00 USD.
- Browser showed Live · SSE and 10 messages sent/handled with no dead-lettered messages in the new app session.

See LIVE-UPDATES.md for event behavior, reconnect handling, and the per-process notification limitation.
