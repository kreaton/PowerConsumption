using PowerConsumption.Data;

namespace PowerConsumption.Tests;

/// <summary>
/// Tests of the aggregation into Danish hours, days, months and years. They use made-up hourly
/// rows, so they need no database; the SQL is covered by running the app against electricity_db.
/// </summary>
public class EnergyAggregatorTests
{
    // A fixed price per kWh: parts that sum to the total, and VAT at 25 %.
    private static readonly PriceBreakdown Price = new(
        new Money(1.0, 0.25), new Money(0.1, 0.025), new Money(0.05, 0.0125),
        new Money(0.05, 0.0125), new Money(0.2, 0.05), new Money(0.01, 0.0025),
        new Money(1.41, 0.3525));

    /// <summary>
    /// One row per hour for the Danish days [from, toExclusive), all with the same kWh, charger kWh,
    /// price and weather (10 °C, 0.1 mm), so expected totals are easy to compute by hand.
    /// </summary>
    private static List<HourlyEnergy> Hours(DateOnly from, DateOnly toExclusive, double kwh = 1, double charger = 0, PriceBreakdown? price = null)
    {
        var list = new List<HourlyEnergy>();
        for (var t = DanishTime.StartOfDayUtc(from); t < DanishTime.StartOfDayUtc(toExclusive); t = t.AddHours(1))
            list.Add(new HourlyEnergy(t, kwh, charger, price ?? Price, new HourlyWeather(10, 0.1, 5, 50, 80)));
        return list;
    }

    // A day is midnight to midnight in Denmark, so it has 23 or 25 hours when the clocks change.
    [Fact]
    public void Days_follow_danish_time_including_the_clock_changes()
    {
        // Summer time ends 25 Oct 2026 (25 hours), starts 29 Mar 2026 (23 hours).
        var from = new DateOnly(2026, 10, 24);
        var to = new DateOnly(2026, 10, 27);
        var periods = EnergyAggregator.Aggregate(Hours(from, to), [], Granularity.Day, from, to);

        Assert.Equal(3, periods.Count);
        Assert.Equal([24, 25, 24], periods.Select(p => p.Hours));
        Assert.All(periods, p => Assert.True(p.IsComplete));
        Assert.Equal(new DateTime(2026, 10, 25), periods[1].Start);
        Assert.Equal(25, periods[1].HouseKwh, 6);

        Assert.Equal(23, DanishTime.HoursBetween(new DateOnly(2026, 3, 29), new DateOnly(2026, 3, 30)));
    }

    // The axis must be stable: a year always shows 12 months, and empty ones are marked incomplete.
    [Fact]
    public void Months_and_years_include_every_period_also_without_data()
    {
        var from = new DateOnly(2026, 1, 1);
        var to = new DateOnly(2027, 1, 1);
        var hours = Hours(new DateOnly(2026, 2, 1), new DateOnly(2026, 3, 1), kwh: 0.5);
        var periods = EnergyAggregator.Aggregate(hours, [], Granularity.Month, from, to);

        Assert.Equal(12, periods.Count);
        Assert.Equal(28 * 24 * 0.5, periods[1].HouseKwh, 6);
        Assert.True(periods[1].IsComplete);
        Assert.Equal(0, periods[0].Hours);
        Assert.False(periods[0].IsComplete);

        var years = EnergyAggregator.Aggregate(hours, [], Granularity.Year, new DateOnly(2025, 1, 1), to);
        Assert.Equal([2025, 2026], years.Select(y => y.Start.Year));
        Assert.Equal(8760, years[0].ExpectedHours);
    }

    // 24 h × 2 kWh, of which 1.5 kWh charging, and one hour without a price: that hour's kWh
    // must be left out of the cost and of the kWh the average price is computed over.
    [Fact]
    public void Cost_is_priced_hour_by_hour_and_the_charger_share_is_split_out()
    {
        var day = new DateOnly(2026, 11, 1);
        var hours = Hours(day, day.AddDays(1), kwh: 2, charger: 1.5);
        hours[3] = hours[3] with { Price = null };

        var total = EnergyAggregator.Total(hours, [], Granularity.Day, day, day.AddDays(1));

        Assert.Equal(48, total.HouseKwh, 6);
        Assert.Equal(36, total.ChargerKwh, 6);
        Assert.Equal(12, total.OtherKwh, 6);
        Assert.Equal(1, total.HoursWithoutPrice);
        Assert.Equal(46, total.PricedKwh, 6);
        Assert.Equal(46 * 1.41, total.Cost!.Value.Total.ExVat, 6);
        Assert.Equal(34.5 * (1.41 + 0.3525), total.ChargerCost!.Value.Total, 6);
        Assert.Equal(1.41, total.AveragePrice!.Value.ExVat, 6);
        Assert.Equal(46, total.Cost.Value.Spot.ExVat, 6);

        // The household without the EV: 0.5 kWh in each of the 23 priced hours.
        Assert.Equal(11.5, total.PricedOtherKwh, 6);
        Assert.Equal(11.5, total.OtherCostParts!.Value.Spot.ExVat, 6);
        Assert.Equal(11.5 * (1.41 + 0.3525), total.OtherCost!.Value.Total, 6);
        Assert.Equal(1.41, total.OtherAveragePrice!.Value.ExVat, 6);
    }

    // Meter and charger are rounded separately, so the charger can read a little more than the meter.
    [Fact]
    public void Charger_exceeding_the_meter_does_not_go_negative()
    {
        var h = new HourlyEnergy(DateTime.UtcNow, 1.0, 1.2, null, null);
        Assert.Equal(0, h.OtherKwh);
    }

    // A session over midnight counts once, on the day it started.
    [Fact]
    public void Sessions_count_where_they_start_and_weather_is_summarised()
    {
        var from = new DateOnly(2026, 11, 1);
        var to = from.AddDays(2);
        var start = DanishTime.StartOfDayUtc(from).AddHours(23.5); // 23:30 on the 1st
        ChargingSession[] sessions = [new("s1", "ZAP", start, start.AddHours(5), 30, null, 0)];

        var periods = EnergyAggregator.Aggregate(Hours(from, to), sessions, Granularity.Day, from, to);

        Assert.Equal([1, 0], periods.Select(p => p.ChargingSessions));
        var w = periods[0].Weather!;
        Assert.Equal(24, w.Hours);
        Assert.Equal(10, w.AverageTemperature!.Value, 6);
        Assert.Equal(2.4, w.Precipitation!.Value, 6);
        Assert.Equal(7, w.HeatingDegreeDays!.Value, 6); // (17 − 10) × 24 h ÷ 24
    }

    // Hours are keyed in UTC, so the two 02:00 hours on the autumn change stay apart.
    [Fact]
    public void Hour_view_has_one_period_per_hour_of_the_day()
    {
        var day = new DateOnly(2026, 10, 25);
        var periods = EnergyAggregator.Aggregate(Hours(day, day.AddDays(1)), [], Granularity.Hour, day, day.AddDays(1));
        Assert.Equal(25, periods.Count);
        // The repeated hour: 02:00 local twice, one hour apart in UTC.
        Assert.Equal(2, periods.Count(p => p.Start.Hour == 2));
    }

    // The view around an anchor day: that day, its month, its year.
    [Fact]
    public void Ranges_for_each_granularity()
    {
        var anchor = new DateOnly(2026, 11, 9);
        Assert.Equal((anchor, anchor.AddDays(1)), EnergyService.RangeFor(Granularity.Hour, anchor));
        Assert.Equal((new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 1)), EnergyService.RangeFor(Granularity.Day, anchor));
        Assert.Equal((new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1)), EnergyService.RangeFor(Granularity.Month, anchor));
    }
}
