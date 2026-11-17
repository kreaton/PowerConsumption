namespace PowerConsumption.Data;

/// <summary>
/// The app's single entry point for data: what the database holds, and reports of consumption,
/// cost, EV charging and weather by hour, day, month or year.
/// </summary>
public sealed class EnergyService(IEnergyRepository repository)
{
    /// <summary>A service reading electricity_db with the given connection settings.</summary>
    public EnergyService(DbSettings settings) : this(new EnergyRepository(new ElectricityDb(settings)))
    {
    }

    /// <summary>What the database holds; used to fill the settings and pick sensible defaults.</summary>
    public Task<DataCatalog> GetCatalogAsync(CancellationToken ct = default) => repository.GetCatalogAsync(ct);

    /// <summary>The Danish days [from, toExclusive), one period per <paramref name="granularity"/>.</summary>
    public async Task<EnergyReport> GetReportAsync(
        EnergyFilter filter,
        Granularity granularity,
        DateOnly from,
        DateOnly toExclusive,
        CancellationToken ct = default)
    {
        if (toExclusive <= from)
            throw new ArgumentException("The period must be at least one day.", nameof(toExclusive));

        var fromUtc = DanishTime.StartOfDayUtc(from);
        var toUtc = DanishTime.StartOfDayUtc(toExclusive);

        // The two queries are independent and each uses its own connection, so run them together.
        var hourlyTask = repository.GetHourlyAsync(filter, fromUtc, toUtc, ct);
        var sessionsTask = repository.GetChargingSessionsAsync(filter.ChargerDevice, fromUtc, toUtc, ct);
        await Task.WhenAll(hourlyTask, sessionsTask);
        var hourly = hourlyTask.Result;
        var sessions = sessionsTask.Result;

        return new EnergyReport(
            granularity,
            from,
            toExclusive,
            EnergyAggregator.Aggregate(hourly, sessions, granularity, from, toExclusive),
            EnergyAggregator.Total(hourly, sessions, granularity, from, toExclusive),
            hourly,
            sessions);
    }

    /// <summary>
    /// The Danish days a view of <paramref name="granularity"/> around <paramref name="anchor"/> covers:
    /// hours → that day, days → its month, months → its year, years → every year with meter data
    /// (taken from <paramref name="catalog"/>; without it, just the anchor's year).
    /// </summary>
    public static (DateOnly From, DateOnly ToExclusive) RangeFor(Granularity granularity, DateOnly anchor, DataCatalog? catalog = null)
    {
        switch (granularity)
        {
            case Granularity.Hour:
                return (anchor, anchor.AddDays(1));
            case Granularity.Day:
                var month = new DateOnly(anchor.Year, anchor.Month, 1);
                return (month, month.AddMonths(1));
            case Granularity.Month:
                var year = new DateOnly(anchor.Year, 1, 1);
                return (year, year.AddYears(1));
            default:
                // All years that have consumption.
                var meter = catalog?.MeteringPoints.FirstOrDefault();
                var first = meter?.FirstDay.Year ?? anchor.Year;
                var last = meter?.LastDay.Year ?? anchor.Year;
                return (new DateOnly(first, 1, 1), new DateOnly(last + 1, 1, 1));
        }
    }
}
