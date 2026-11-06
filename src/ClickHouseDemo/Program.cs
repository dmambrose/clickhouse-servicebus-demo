using System.Diagnostics;
using System.Globalization;
using ClickHouse.Driver;
using ClickHouse.Driver.ADO.Parameters;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
var token = cancellation.Token;

try
{
    var connectionString = Environment.GetEnvironmentVariable("CLICKHOUSE_CONNECTION_STRING")
        ?? "Host=localhost;Port=8123;Protocol=http;Database=demo;Username=demo;Password=demo_password;UseCustomDecimals=false;Timeout=30";
    using var client = new ClickHouseClient(connectionString);
    Console.WriteLine($"ClickHouse version: {await client.ExecuteScalarAsync("SELECT version()", cancellationToken: token)}");

    // The official Docker image creates the demo database and user at startup.
    var schema = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "sql", "schema.sql"), token);
    await client.ExecuteNonQueryAsync(schema, cancellationToken: token);

    // A separate run ID lets us rerun without deleting or mixing existing data.
    var runId = Guid.NewGuid();
    var events = SampleData.CreateEvents(runId, 10_000);
    var parameters = new ClickHouseParameterCollection();
    parameters.Add(new ClickHouseDbParameter { ParameterName = "runId", Value = runId });
    Console.WriteLine($"Run ID: {runId}");
    var timer = Stopwatch.StartNew();
    var inserted = await client.InsertBinaryAsync(
        "demo.sales_events",
        new[] { "run_id", "event_id", "occurred_at", "product", "country", "quantity", "unit_price" },
        events.Select(e => new object[] { e.RunId, e.EventId, e.OccurredAt, e.Product, e.Country, e.Quantity, e.UnitPrice }),
        cancellationToken: token);
    Console.WriteLine($"Inserted {inserted:N0} events in {timer.ElapsedMilliseconds:N0} ms.\n");

    await PrintReport("Revenue by product", """
        SELECT product, count() AS events, sum(quantity) AS units,
               sum(quantity * unit_price) AS revenue
        FROM demo.sales_events WHERE run_id = {runId:UUID}
        GROUP BY product ORDER BY revenue DESC, product
        """);

    await PrintReport("Daily revenue (UTC)", """
        SELECT toString(toDate(occurred_at)) AS day, count() AS events,
               sum(quantity * unit_price) AS revenue
        FROM demo.sales_events WHERE run_id = {runId:UUID}
        GROUP BY day ORDER BY day
        """);

    // Bind values through typed parameters rather than concatenating user input.
    var countryParameters = new ClickHouseParameterCollection();
    countryParameters.Add(new ClickHouseDbParameter { ParameterName = "runId", Value = runId });
    countryParameters.Add(new ClickHouseDbParameter { ParameterName = "country", Value = "US" });
    var usCount = await client.ExecuteScalarAsync("""
        SELECT count() FROM demo.sales_events
        WHERE run_id = {runId:UUID} AND country = {country:String}
        """, countryParameters, cancellationToken: token);
    Console.WriteLine($"Parameterized country filter: US = {usCount:N0} events");

    using var totals = await client.ExecuteReaderAsync("""
        SELECT count(), sum(quantity * unit_price)
        FROM demo.sales_events WHERE run_id = {runId:UUID}
        """, parameters, cancellationToken: token);
    if (!await totals.ReadAsync(token)) throw new InvalidOperationException("No totals returned.");
    var count = Convert.ToInt64(totals.GetValue(0), CultureInfo.InvariantCulture);
    var revenue = Convert.ToDecimal(totals.GetValue(1), CultureInfo.InvariantCulture);
    var expectedRevenue = events.Sum(e => e.Quantity * e.UnitPrice);
    var expectedUsCount = events.Count(e => e.Country == "US");
    if (inserted != events.Count || count != events.Count || revenue != expectedRevenue
        || Convert.ToInt64(usCount, CultureInfo.InvariantCulture) != expectedUsCount)
        throw new InvalidOperationException("Stored results do not match the generated sample data.");

    Console.WriteLine($"PASS: {count:N0} events, revenue {revenue:N2}, and US count match the input.");
    return 0;

    async Task PrintReport(string title, string sql)
    {
        Console.WriteLine(title);
        using var reader = await client.ExecuteReaderAsync(sql, parameters, cancellationToken: token);
        Console.WriteLine(string.Join(" | ", Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)));
        while (await reader.ReadAsync(token))
            Console.WriteLine(string.Join(" | ", Enumerable.Range(0, reader.FieldCount)
                .Select(i => Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))));
        Console.WriteLine();
    }
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Demo canceled or its two-minute time limit expired.");
    return 1;
}
catch (Exception ex)
{
    // Exception messages can contain credentials; report only the failure type.
    Console.Error.WriteLine($"Demo failed ({ex.GetType().Name}). Check that ClickHouse is healthy and the connection settings are correct.");
    Console.Error.WriteLine("Start the server with: docker compose up -d --wait clickhouse");
    return 1;
}
