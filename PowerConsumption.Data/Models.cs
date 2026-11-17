namespace PowerConsumption.Data;

/// <summary>How finely to aggregate: one bar per hour, day, month or year (Danish time).</summary>
/// <remarks>The order matters: the app zooms in with <c>granularity - 1</c> and out with <c>granularity + 1</c>.</remarks>
public enum Granularity
{
    Hour,
    Day,
    Month,
    Year
}

/// <summary>Which meter, charger and weather location to read.</summary>
/// <param name="MeteringPointId">The Eloverblik metering point (<c>mrid</c>).</param>
/// <param name="ChargerDevice">A Zaptec <c>device_name</c>, or null for all chargers together.</param>
/// <param name="WeatherLocation">An Open-Meteo <c>location</c>, or null for no weather.</param>
public sealed record EnergyFilter(string MeteringPointId, string? ChargerDevice = null, string? WeatherLocation = null);

/// <summary>The weather in one hour (Open-Meteo). Any value can be missing.</summary>
/// <param name="Temperature">Air temperature 2 m above ground, °C.</param>
/// <param name="Precipitation">Rain, snow etc. in the hour, mm.</param>
/// <param name="WindSpeed">Wind speed 10 m above ground, km/h.</param>
/// <param name="CloudCover">Cloud cover, %.</param>
/// <param name="Humidity">Relative humidity 2 m above ground, %.</param>
public sealed record HourlyWeather(
    double? Temperature,
    double? Precipitation,
    double? WindSpeed,
    double? CloudCover,
    double? Humidity);

/// <summary>
/// One hour of the house's metered consumption, with the part the EV charger used, the price
/// of that hour (per kWh) and the weather. <see cref="HouseKwh"/> is the whole meter and
/// includes the charger.
/// </summary>
/// <param name="HourUtc">Start of the hour, UTC.</param>
/// <param name="HouseKwh">The meter's reading for the hour, kWh (charger included).</param>
/// <param name="ChargerKwh">What the charger(s) delivered in the hour, kWh; 0 when not charging.</param>
/// <param name="Price">The price per kWh in the hour, or null when Strømligning has none.</param>
/// <param name="Weather">The weather in the hour, or null when there is no weather row.</param>
public sealed record HourlyEnergy(
    DateTime HourUtc,
    double HouseKwh,
    double ChargerKwh,
    PriceBreakdown? Price,
    HourlyWeather? Weather)
{
    /// <summary>Everything but the charger. Never negative, although rounding can make the charger exceed the meter.</summary>
    public double OtherKwh => Math.Max(0, HouseKwh - ChargerKwh);
}

/// <summary>One Zaptec charging session, with its energy and what it cost at the hourly prices.</summary>
/// <param name="SessionId">Zaptec's session id.</param>
/// <param name="DeviceName">The charger, e.g. "ZAP123456".</param>
/// <param name="StartUtc">When the car was connected, UTC.</param>
/// <param name="EndUtc">When the session ended, UTC (can be long after charging stopped).</param>
/// <param name="Kwh">Energy delivered in the session.</param>
/// <param name="Cost">Each hour's energy × that hour's price, summed; null when no hour had a price.</param>
/// <param name="HoursWithoutPrice">Readings in the session whose hour had no price (not included in <paramref name="Cost"/>).</param>
public sealed record ChargingSession(
    string SessionId,
    string DeviceName,
    DateTime StartUtc,
    DateTime EndUtc,
    double Kwh,
    Money? Cost,
    int HoursWithoutPrice)
{
    /// <summary>Start in Danish wall-clock time.</summary>
    public DateTime StartLocal => DanishTime.ToLocal(StartUtc);

    /// <summary>End in Danish wall-clock time.</summary>
    public DateTime EndLocal => DanishTime.ToLocal(EndUtc);
}

/// <summary>A source of data and the UTC period it covers.</summary>
/// <param name="Name">The meter id, charger name, weather location, or "price".</param>
/// <param name="FirstUtc">The oldest timestamp, UTC.</param>
/// <param name="LastUtc">The newest timestamp, UTC.</param>
/// <param name="Count">Rows (hours) for meters and weather, sessions for chargers.</param>
public sealed record SourceRange(string Name, DateTime FirstUtc, DateTime LastUtc, long Count)
{
    /// <summary>The Danish calendar day of <see cref="FirstUtc"/>.</summary>
    public DateOnly FirstDay => DateOnly.FromDateTime(DanishTime.ToLocal(FirstUtc));

    /// <summary>The Danish calendar day of <see cref="LastUtc"/>.</summary>
    public DateOnly LastDay => DateOnly.FromDateTime(DanishTime.ToLocal(LastUtc));
}

/// <summary>What the database holds: meters, chargers, weather locations and the price period.</summary>
/// <param name="MeteringPoints">Meters, the one with most readings first.</param>
/// <param name="Chargers">Zaptec chargers, the one with most sessions first.</param>
/// <param name="WeatherLocations">Open-Meteo locations, the one with most hours first.</param>
/// <param name="Prices">The period with prices, or null when there are none.</param>
public sealed record DataCatalog(
    IReadOnlyList<SourceRange> MeteringPoints,
    IReadOnlyList<SourceRange> Chargers,
    IReadOnlyList<SourceRange> WeatherLocations,
    SourceRange? Prices);
