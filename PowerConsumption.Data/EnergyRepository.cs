using Dapper;

namespace PowerConsumption.Data;

/// <summary>
/// Raw reads from electricity_db. All times are UTC; periods are [from, to).
/// <see cref="EnergyService"/> builds on this; tests can substitute it to run without a database.
/// </summary>
public interface IEnergyRepository
{
    /// <summary>What the database holds: the meters, chargers and weather locations, each with its first and last timestamp.</summary>
    Task<DataCatalog> GetCatalogAsync(CancellationToken ct = default);

    /// <summary>One row per metered hour in [fromUtc, toUtc), with charger energy, price and weather of that hour.</summary>
    Task<IReadOnlyList<HourlyEnergy>> GetHourlyAsync(EnergyFilter filter, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>Charging sessions that started in [fromUtc, toUtc).</summary>
    Task<IReadOnlyList<ChargingSession>> GetChargingSessionsAsync(string? chargerDevice, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
}

/// <summary>
/// Dapper queries against the tables MultiApiFetcher maintains:
/// <c>eloverblik_energy_readings</c> (hourly meter readings, timestamp = start of the hour),
/// <c>stromligning_price_data</c> (one row per price part per hour, timestamp = start of the hour),
/// <c>zaptec_charging_sessions</c> (energy per session per hour, timestamp = END of the interval) and
/// <c>openmeteo_weather_hourly</c>.
/// <para>
/// The tables belong to MultiApiFetcher, so this class only reads; it adds no indexes or views.
/// Every call opens its own short-lived connection (MySqlConnector pools them), which also lets
/// <see cref="EnergyService"/> run two queries in parallel.
/// </para>
/// </summary>
public sealed class EnergyRepository(ElectricityDb db) : IEnergyRepository
{
    // Zaptec stamps each reading at the end of its interval (12:00 for 11:00–12:00, or the
    // session end inside the last hour). One second back and down to the hour gives the start
    // of the hour it belongs to, the same key the meter and the prices use.
    private const string ZaptecHourExpr =
        "CAST(DATE_FORMAT(charge_date_time_utc - INTERVAL 1 SECOND, '%Y-%m-%d %H:00:00') AS DATETIME(6))";

    // Strømligning stores one row per price part per hour ("long" format). This turns them into
    // one row per hour with a column pair per part: "value" is excl. VAT, "total" incl. VAT.
    // "price" is Strømligning's own sum of the parts. MAX() only picks the single matching row;
    // a part that is missing for an hour comes out as NULL.
    private const string PricePivot = """
        SELECT date_time_utc,
               MAX(CASE WHEN price_type = 'electricity'    THEN value END) AS SpotExVat,
               MAX(CASE WHEN price_type = 'electricity'    THEN total END) AS SpotIncl,
               MAX(CASE WHEN price_type = 'surcharge'      THEN value END) AS SurchargeExVat,
               MAX(CASE WHEN price_type = 'surcharge'      THEN total END) AS SurchargeIncl,
               MAX(CASE WHEN price_type = 'netTariff'      THEN value END) AS NetTariffExVat,
               MAX(CASE WHEN price_type = 'netTariff'      THEN total END) AS NetTariffIncl,
               MAX(CASE WHEN price_type = 'systemTariff'   THEN value END) AS SystemTariffExVat,
               MAX(CASE WHEN price_type = 'systemTariff'   THEN total END) AS SystemTariffIncl,
               MAX(CASE WHEN price_type = 'distribution'   THEN value END) AS DistributionExVat,
               MAX(CASE WHEN price_type = 'distribution'   THEN total END) AS DistributionIncl,
               MAX(CASE WHEN price_type = 'electricityTax' THEN value END) AS ElectricityTaxExVat,
               MAX(CASE WHEN price_type = 'electricityTax' THEN total END) AS ElectricityTaxIncl,
               MAX(CASE WHEN price_type = 'price'          THEN value END) AS TotalExVat,
               MAX(CASE WHEN price_type = 'price'          THEN total END) AS TotalIncl
        FROM stromligning_price_data
        WHERE date_time_utc >= @FromUtc AND date_time_utc < @ToUtc
        GROUP BY date_time_utc
        """;

    // One row per metered hour. The meter drives the query: hours without a reading are left out
    // (the aggregator counts them as missing), while a missing price, charger reading or weather
    // just leaves NULLs through the LEFT JOINs.
    // The Zaptec window is (from, to] rather than [from, to) because its timestamps are interval
    // ends: a reading stamped exactly at "to" belongs to the last hour inside the range.
    // "@Charger IS NULL" means all chargers; a NULL @Location matches no weather rows.
    private static readonly string HourlySql = $"""
        SELECT e.dt_measured_utc       AS HourUtc,
               e.quantity              AS HouseKwh,
               z.ChargerKwh,
               p.SpotExVat, p.SpotIncl, p.SurchargeExVat, p.SurchargeIncl,
               p.NetTariffExVat, p.NetTariffIncl, p.SystemTariffExVat, p.SystemTariffIncl,
               p.DistributionExVat, p.DistributionIncl, p.ElectricityTaxExVat, p.ElectricityTaxIncl,
               p.TotalExVat, p.TotalIncl,
               w.temperature_2m        AS Temperature,
               w.precipitation         AS Precipitation,
               w.wind_speed_10m        AS WindSpeed,
               w.cloud_cover           AS CloudCover,
               w.relative_humidity_2m  AS Humidity,
               w.time_utc IS NOT NULL  AS HasWeather
        FROM eloverblik_energy_readings e
        LEFT JOIN ({PricePivot}) p
               ON p.date_time_utc = e.dt_measured_utc
        LEFT JOIN (
            SELECT {ZaptecHourExpr} AS hour_utc, SUM(energy) AS ChargerKwh
            FROM zaptec_charging_sessions
            WHERE charge_date_time_utc > @FromUtc AND charge_date_time_utc <= @ToUtc
              AND (@Charger IS NULL OR device_name = @Charger)
            GROUP BY hour_utc
        ) z ON z.hour_utc = e.dt_measured_utc
        LEFT JOIN openmeteo_weather_hourly w
               ON w.time_utc = e.dt_measured_utc AND w.location = @Location
        WHERE e.mrid = @MeteringPoint
          AND e.dt_measured_utc >= @FromUtc AND e.dt_measured_utc < @ToUtc
        ORDER BY e.dt_measured_utc
        """;

    // One row per charging session, with its energy priced hour by hour at Strømligning's total.
    // A session belongs to the range it STARTED in, so the filter is on MIN(start) in HAVING;
    // the table is small (about a thousand rows), so grouping all of it is cheap.
    // HoursWithoutPrice counts readings whose hour has no price (their energy adds no cost).
    private static readonly string SessionsSql = $"""
        SELECT z.session_id                                  AS SessionId,
               MAX(z.device_name)                            AS DeviceName,
               MIN(z.start_date_time_utc)                    AS StartUtc,
               MAX(z.end_date_time_utc)                      AS EndUtc,
               SUM(z.energy)                                 AS Kwh,
               SUM(z.energy * p.value)                       AS CostExVat,
               SUM(z.energy * (p.total - p.value))           AS CostVat,
               SUM(CASE WHEN p.date_time_utc IS NULL THEN 1 ELSE 0 END) AS HoursWithoutPrice
        FROM zaptec_charging_sessions z
        LEFT JOIN stromligning_price_data p
               ON p.price_type = 'price' AND p.date_time_utc = {ZaptecHourExpr.Replace("charge_date_time_utc", "z.charge_date_time_utc")}
        WHERE (@Charger IS NULL OR z.device_name = @Charger)
        GROUP BY z.session_id
        HAVING StartUtc >= @FromUtc AND StartUtc < @ToUtc
        ORDER BY StartUtc
        """;

    // Four result sets read with QueryMultiple: meters, chargers, weather locations (each the
    // busiest first, so the first one is a sensible default) and the overall price period.
    // For chargers, Count is the number of sessions rather than rows.
    private const string CatalogSql = """
        SELECT mrid AS Name, MIN(dt_measured_utc) AS FirstUtc, MAX(dt_measured_utc) AS LastUtc, COUNT(*) AS Count
        FROM eloverblik_energy_readings GROUP BY mrid ORDER BY Count DESC;

        SELECT device_name AS Name, MIN(charge_date_time_utc) AS FirstUtc, MAX(charge_date_time_utc) AS LastUtc, COUNT(DISTINCT session_id) AS Count
        FROM zaptec_charging_sessions GROUP BY device_name ORDER BY Count DESC;

        SELECT location AS Name, MIN(time_utc) AS FirstUtc, MAX(time_utc) AS LastUtc, COUNT(*) AS Count
        FROM openmeteo_weather_hourly GROUP BY location ORDER BY Count DESC;

        SELECT 'price' AS Name, MIN(date_time_utc) AS FirstUtc, MAX(date_time_utc) AS LastUtc, COUNT(*) AS Count
        FROM stromligning_price_data WHERE price_type = 'price' HAVING COUNT(*) > 0;
        """;

    /// <inheritdoc />
    public async Task<DataCatalog> GetCatalogAsync(CancellationToken ct = default)
    {
        // The result sets must be read in the order the statements appear in CatalogSql.
        await using var connection = await db.OpenAsync(ct);
        await using var grid = await connection.QueryMultipleAsync(new CommandDefinition(CatalogSql, cancellationToken: ct));
        var meters = (await grid.ReadAsync<RangeRow>()).Select(r => r.ToRange()).ToList();
        var chargers = (await grid.ReadAsync<RangeRow>()).Select(r => r.ToRange()).ToList();
        var locations = (await grid.ReadAsync<RangeRow>()).Select(r => r.ToRange()).ToList();
        var prices = (await grid.ReadAsync<RangeRow>()).Select(r => r.ToRange()).FirstOrDefault();
        return new DataCatalog(meters, chargers, locations, prices);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HourlyEnergy>> GetHourlyAsync(EnergyFilter filter, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var rows = await connection.QueryAsync<HourlyRow>(new CommandDefinition(HourlySql, new
        {
            FromUtc = Utc(fromUtc),
            ToUtc = Utc(toUtc),
            MeteringPoint = filter.MeteringPointId,
            Charger = filter.ChargerDevice,
            Location = filter.WeatherLocation
        }, cancellationToken: ct));
        return rows.Select(r => r.ToHourly()).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChargingSession>> GetChargingSessionsAsync(string? chargerDevice, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var rows = await connection.QueryAsync<SessionRow>(new CommandDefinition(SessionsSql, new
        {
            FromUtc = Utc(fromUtc),
            ToUtc = Utc(toUtc),
            Charger = chargerDevice
        }, cancellationToken: ct));
        return rows.Select(r => r.ToSession()).ToList();
    }

    /// <summary>
    /// Marks a time as UTC. Parameters must be UTC because the connection uses
    /// <c>DateTimeKind=Utc</c>, and values read back are UTC whatever kind they arrive with.
    /// An Unspecified time is taken to be UTC already, not converted.
    /// </summary>
    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    // ---- Dapper row shapes ----
    // Dapper maps columns to properties by name (the SQL aliases above). Decimal columns stay
    // decimal here so nothing is lost in the mapping; they become double in the public model,
    // which is plenty for kWh and kroner and keeps the arithmetic simple.

    /// <summary>One line of the catalog: a source name and the period it covers.</summary>
    private sealed class RangeRow
    {
        public string Name { get; init; } = "";
        public DateTime FirstUtc { get; init; }
        public DateTime LastUtc { get; init; }
        public long Count { get; init; }

        public SourceRange ToRange() => new(Name, Utc(FirstUtc), Utc(LastUtc), Count);
    }

    /// <summary>One row of <see cref="HourlySql"/>; "…ExVat" is the price part excl. VAT, "…Incl" incl. VAT, per kWh.</summary>
    internal sealed class HourlyRow
    {
        public DateTime HourUtc { get; init; }
        public decimal HouseKwh { get; init; }
        public decimal? ChargerKwh { get; init; }
        public decimal? SpotExVat { get; init; }
        public decimal? SpotIncl { get; init; }
        public decimal? SurchargeExVat { get; init; }
        public decimal? SurchargeIncl { get; init; }
        public decimal? NetTariffExVat { get; init; }
        public decimal? NetTariffIncl { get; init; }
        public decimal? SystemTariffExVat { get; init; }
        public decimal? SystemTariffIncl { get; init; }
        public decimal? DistributionExVat { get; init; }
        public decimal? DistributionIncl { get; init; }
        public decimal? ElectricityTaxExVat { get; init; }
        public decimal? ElectricityTaxIncl { get; init; }
        public decimal? TotalExVat { get; init; }
        public decimal? TotalIncl { get; init; }
        public double? Temperature { get; init; }
        public double? Precipitation { get; init; }
        public double? WindSpeed { get; init; }
        public double? CloudCover { get; init; }
        public double? Humidity { get; init; }
        /// <summary>True when the weather join found a row; every weather value can be NULL even then.</summary>
        public bool HasWeather { get; init; }

        /// <summary>Converts to the public model; a missing charger reading means the charger used nothing.</summary>
        public HourlyEnergy ToHourly()
        {
            // An hour is priced when Strømligning's total is there; a missing part counts as 0.
            PriceBreakdown? price = TotalExVat is null
                ? null
                : new PriceBreakdown(
                    M(SpotExVat, SpotIncl), M(SurchargeExVat, SurchargeIncl), M(NetTariffExVat, NetTariffIncl),
                    M(SystemTariffExVat, SystemTariffIncl), M(DistributionExVat, DistributionIncl),
                    M(ElectricityTaxExVat, ElectricityTaxIncl), M(TotalExVat, TotalIncl));
            var weather = HasWeather ? new HourlyWeather(Temperature, Precipitation, WindSpeed, CloudCover, Humidity) : null;
            return new HourlyEnergy(Utc(HourUtc), (double)HouseKwh, (double)(ChargerKwh ?? 0), price, weather);

            // Value and total are each rounded to two decimals in the database; the total is used
            // as stored (it is what MultiApiFetcher's own queries use) and VAT is the difference.
            static Money M(decimal? exVat, decimal? total) =>
                new((double)(exVat ?? 0), (double)((total ?? exVat ?? 0) - (exVat ?? 0)));
        }
    }

    /// <summary>One row of <see cref="SessionsSql"/>. Cost is NULL only when no hour of the session had a price.</summary>
    private sealed class SessionRow
    {
        public string SessionId { get; init; } = "";
        public string? DeviceName { get; init; }
        public DateTime StartUtc { get; init; }
        public DateTime EndUtc { get; init; }
        public decimal Kwh { get; init; }
        public decimal? CostExVat { get; init; }
        public decimal? CostVat { get; init; }
        public long HoursWithoutPrice { get; init; }

        public ChargingSession ToSession() => new(
            SessionId, DeviceName ?? "", Utc(StartUtc), Utc(EndUtc), (double)Kwh,
            CostExVat is null ? null : new Money((double)CostExVat, (double)(CostVat ?? 0)),
            (int)HoursWithoutPrice);
    }
}
