internal static class SampleData
{
public static List<SalesEvent> CreateEvents(Guid runId, int count)
{
    var products = new[] { ("Keyboard", 79.90m), ("Mouse", 29.50m), ("Monitor", 249.00m), ("Headset", 59.95m) };
    var countries = new[] { "US", "GB", "DE", "CA" };
    var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    var random = new Random(42);
    return Enumerable.Range(0, count).Select(i =>
    {
        var product = products[random.Next(products.Length)];
        return new SalesEvent(runId, (ulong)i + 1, start.AddSeconds(random.Next(7 * 24 * 60 * 60)),
            product.Item1, countries[random.Next(countries.Length)], (ushort)random.Next(1, 6), product.Item2);
    }).ToList();
}

}

internal sealed record SalesEvent(Guid RunId, ulong EventId, DateTime OccurredAt,
    string Product, string Country, ushort Quantity, decimal UnitPrice);
