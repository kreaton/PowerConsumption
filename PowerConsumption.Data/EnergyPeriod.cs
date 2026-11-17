namespace PowerConsumption.Data;

/// <summary>Weather summed up over a period: averages, extremes and totals of the hours that had weather.</summary>
/// <param name="Hours">Hours with a weather row; fewer than the period's hours when weather data lags behind.</param>
/// <param name="AverageTemperature">Mean of the hourly temperatures, °C.</param>
/// <param name="MinTemperature">Coldest hour, °C.</param>
/// <param name="MaxTemperature">Warmest hour, °C.</param>
/// <param name="Precipitation">Total precipitation, mm.</param>
/// <param name="AverageWindSpeed">Mean wind speed, km/h.</param>
/// <param name="AverageCloudCover">Mean cloud cover, %.</param>
/// <param name="HeatingDegreeDays">
/// Graddage: Σ max(0, <see cref="DegreeDayBase"/> − hourly temperature) ÷ 24. A rough measure of how
/// much heating the period needed, useful for comparing consumption across winters.
/// </param>
public sealed record WeatherSummary(
    int Hours,
    double? AverageTemperature,
    double? MinTemperature,
    double? MaxTemperature,
    double? Precipitation,
    double? AverageWindSpeed,
    double? AverageCloudCover,
    double? HeatingDegreeDays)
{
    /// <summary>Base temperature for heating degree days, as the Danish "graddage" use it.</summary>
    public const double DegreeDayBase = 17.0;
}

/// <summary>
/// Consumption over one hour, day, month or year (Danish time), with what it cost, how much of
/// it went to the EV charger, and the weather. Costs are kWh × that hour's price, summed hour by
/// hour, so they reflect when the power was used.
/// </summary>
public sealed class EnergyPeriod
{
    /// <summary>Whether this is an hour, day, month or year (a report's total carries the report's granularity).</summary>
    public required Granularity Granularity { get; init; }

    /// <summary>Danish wall-clock start and end (end exclusive).</summary>
    public required DateTime Start { get; init; }
    public required DateTime End { get; init; }

    /// <summary>Start as an instant; unambiguous also in the repeated hour when summer time ends.</summary>
    public required DateTime StartUtc { get; init; }

    /// <summary>Metered hours in the period.</summary>
    public required int Hours { get; init; }

    /// <summary>Hours the period has on the calendar (23–25 for a day). More than <see cref="Hours"/> when data is missing.</summary>
    public required int ExpectedHours { get; init; }

    /// <summary>Every hour of the period has a meter reading (false for the current month, or future days).</summary>
    public bool IsComplete => Hours >= ExpectedHours;

    /// <summary>The whole meter, charger included.</summary>
    public required double HouseKwh { get; init; }

    /// <summary>What the EV charger(s) delivered.</summary>
    public required double ChargerKwh { get; init; }

    /// <summary>The household without the charger: Σ hourly max(0, meter − charger).</summary>
    public required double OtherKwh { get; init; }

    /// <summary>The whole meter's cost, split into price parts; null when no hour had a price.</summary>
    public PriceBreakdown? Cost { get; init; }

    /// <summary>What the charger's share cost; null when no charging hour had a price.</summary>
    public Money? ChargerCost { get; init; }

    /// <summary>kWh in the hours that had a price; the divisor for <see cref="AveragePrice"/>.</summary>
    public required double PricedKwh { get; init; }

    /// <summary>Charger kWh in the hours that had a price; the divisor for <see cref="ChargerAveragePrice"/>.</summary>
    public required double PricedChargerKwh { get; init; }

    /// <summary>Metered hours with no price; their kWh are not in <see cref="Cost"/>.</summary>
    public required int HoursWithoutPrice { get; init; }

    /// <summary>Charging sessions that started in the period.</summary>
    public required int ChargingSessions { get; init; }

    /// <summary>The weather over the period, or null when no hour had weather.</summary>
    public WeatherSummary? Weather { get; init; }

    /// <summary>
    /// What the household without the charger cost, split into price parts: each hour's
    /// <see cref="HourlyEnergy.OtherKwh"/> × that hour's price. Null when no hour had a price.
    /// </summary>
    public PriceBreakdown? OtherCostParts { get; init; }

    /// <summary>Household (excl. EV) kWh in the hours that had a price; the divisor for <see cref="OtherAveragePrice"/>.</summary>
    public double PricedOtherKwh { get; init; }

    /// <summary>What the household without the charger cost, in total.</summary>
    public Money? OtherCost => OtherCostParts?.Total;

    /// <summary>Average price paid per kWh, weighted by consumption.</summary>
    public Money? AveragePrice => Cost is { } c && PricedKwh > 0 ? c.Total.Scale(1 / PricedKwh) : null;

    /// <summary>Average price per kWh for the household without the charger, weighted by its consumption.</summary>
    public Money? OtherAveragePrice => OtherCost is { } c && PricedOtherKwh > 0 ? c.Scale(1 / PricedOtherKwh) : null;

    /// <summary>Average price paid per kWh charged.</summary>
    public Money? ChargerAveragePrice => ChargerCost is { } c && PricedChargerKwh > 0 ? c.Scale(1 / PricedChargerKwh) : null;
}

/// <summary>The periods of one view, and the whole of it summed into one.</summary>
/// <param name="Granularity">The size of each period.</param>
/// <param name="From">First Danish day of the view.</param>
/// <param name="ToExclusive">The day after the last day of the view.</param>
/// <param name="Periods">Every period in the view in time order, also those without data.</param>
/// <param name="Total">All of the view as one period, for the summary figures and the table's total line.</param>
/// <param name="Hourly">The hourly rows the periods were built from.</param>
/// <param name="Sessions">Charging sessions that started in the view.</param>
public sealed record EnergyReport(
    Granularity Granularity,
    DateOnly From,
    DateOnly ToExclusive,
    IReadOnlyList<EnergyPeriod> Periods,
    EnergyPeriod Total,
    IReadOnlyList<HourlyEnergy> Hourly,
    IReadOnlyList<ChargingSession> Sessions)
{
    /// <summary>The newest metered hour in the report, in Danish time.</summary>
    public DateTime? DataUntil => Hourly.Count == 0 ? null : DanishTime.ToLocal(Hourly[^1].HourUtc).AddHours(1);
}
