namespace PowerConsumption.Data;

/// <summary>
/// Sums hourly rows into Danish hours, days, months or years. The database has no time-zone
/// tables, so the grouping is done here rather than in SQL; it also gets the 23- and 25-hour
/// days right.
/// </summary>
public static class EnergyAggregator
{
    /// <summary>
    /// One period per hour/day/month/year in [from, toExclusive), also the ones without data, so
    /// a month always has all its days. Sessions count in the period they started in.
    /// </summary>
    public static IReadOnlyList<EnergyPeriod> Aggregate(
        IReadOnlyList<HourlyEnergy> hourly,
        IReadOnlyList<ChargingSession> sessions,
        Granularity granularity,
        DateOnly from,
        DateOnly toExclusive)
    {
        var rangeStart = DanishTime.StartOfDayUtc(from);
        var rangeEnd = DanishTime.StartOfDayUtc(toExclusive);

        // Bucket hours and sessions by the UTC start of their period; the same key comes out of
        // Periods() below, so every period finds its rows with a lookup.
        var hoursByPeriod = hourly.ToLookup(h => PeriodStartUtc(h.HourUtc, granularity));
        var sessionsByPeriod = sessions.ToLookup(s => PeriodStartUtc(s.StartUtc, granularity));

        var result = new List<EnergyPeriod>();
        foreach (var (startUtc, endUtc) in Periods(granularity, from, toExclusive))
        {
            // Clip to the range, so a period sticking out of it expects only the hours inside.
            var start = startUtc < rangeStart ? rangeStart : startUtc;
            var end = endUtc > rangeEnd ? rangeEnd : endUtc;
            result.Add(Build(granularity, start, end, hoursByPeriod[startUtc].ToList(), sessionsByPeriod[startUtc].Count()));
        }
        return result;
    }

    /// <summary>Everything in the range summed into one period.</summary>
    public static EnergyPeriod Total(
        IReadOnlyList<HourlyEnergy> hourly,
        IReadOnlyList<ChargingSession> sessions,
        Granularity granularity,
        DateOnly from,
        DateOnly toExclusive) =>
        Build(granularity, DanishTime.StartOfDayUtc(from), DanishTime.StartOfDayUtc(toExclusive), hourly, sessions.Count);

    /// <summary>The UTC start of the hour/day/month/year (Danish) that an instant falls in.</summary>
    public static DateTime PeriodStartUtc(DateTime utc, Granularity granularity)
    {
        // Hours are keyed in UTC: Denmark's offsets are whole hours, so a UTC hour is a local
        // hour, and UTC keeps the two 02:00 hours apart on the day summer time ends.
        if (granularity == Granularity.Hour)
            return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);

        var local = DanishTime.ToLocal(utc);
        var day = granularity switch
        {
            Granularity.Day => DateOnly.FromDateTime(local),
            Granularity.Month => new DateOnly(local.Year, local.Month, 1),
            _ => new DateOnly(local.Year, 1, 1)
        };
        return DanishTime.StartOfDayUtc(day);
    }

    /// <summary>The periods covering [from, toExclusive), as UTC [start, end). The first and last may stick out of the range.</summary>
    public static IEnumerable<(DateTime StartUtc, DateTime EndUtc)> Periods(Granularity granularity, DateOnly from, DateOnly toExclusive)
    {
        if (granularity == Granularity.Hour)
        {
            // Step in UTC so the 23- and 25-hour days get exactly their hours.
            var end = DanishTime.StartOfDayUtc(toExclusive);
            for (var t = DanishTime.StartOfDayUtc(from); t < end; t = t.AddHours(1))
                yield return (t, t.AddHours(1));
            yield break;
        }

        // Days, months and years: step through Danish calendar dates, starting at the period
        // that contains "from", and convert each boundary (local midnight) to UTC.
        var day = granularity switch
        {
            Granularity.Day => from,
            Granularity.Month => new DateOnly(from.Year, from.Month, 1),
            _ => new DateOnly(from.Year, 1, 1)
        };
        while (day < toExclusive)
        {
            var next = granularity switch
            {
                Granularity.Day => day.AddDays(1),
                Granularity.Month => day.AddMonths(1),
                _ => day.AddYears(1)
            };
            yield return (DanishTime.StartOfDayUtc(day), DanishTime.StartOfDayUtc(next));
            day = next;
        }
    }

    /// <summary>Sums the hours of one period [startUtc, endUtc).</summary>
    private static EnergyPeriod Build(Granularity granularity, DateTime startUtc, DateTime endUtc, IReadOnlyList<HourlyEnergy> hours, int sessions)
    {
        // Costs start as null and only become a value once an hour has a price, so "no price at
        // all" (null) stays distinct from "priced, but nothing used" (0 kr).
        PriceBreakdown? cost = null, otherCost = null;
        Money? chargerCost = null;
        double pricedKwh = 0, pricedChargerKwh = 0, pricedOtherKwh = 0;
        var withoutPrice = 0;
        foreach (var h in hours)
        {
            if (h.Price is not { } price)
            {
                withoutPrice++;
                continue;
            }
            // Hour by hour: this hour's kWh × this hour's price, every part of it.
            cost = (cost ?? PriceBreakdown.Zero) + price.Scale(h.HouseKwh);
            pricedKwh += h.HouseKwh;
            // The household without the EV, priced the same way. Priced from OtherKwh (never
            // negative) rather than as cost − EV cost, so it matches the household kWh exactly.
            otherCost = (otherCost ?? PriceBreakdown.Zero) + price.Scale(h.OtherKwh);
            pricedOtherKwh += h.OtherKwh;
            if (h.ChargerKwh > 0)
            {
                // The charger's share is priced at the same hour's total price.
                chargerCost = (chargerCost ?? Money.Zero) + price.Total.Scale(h.ChargerKwh);
                pricedChargerKwh += h.ChargerKwh;
            }
        }

        return new EnergyPeriod
        {
            Granularity = granularity,
            Start = DanishTime.ToLocal(startUtc),
            End = DanishTime.ToLocal(endUtc),
            StartUtc = startUtc,
            Hours = hours.Count,
            ExpectedHours = (int)Math.Round((endUtc - startUtc).TotalHours),
            HouseKwh = hours.Sum(h => h.HouseKwh),
            ChargerKwh = hours.Sum(h => h.ChargerKwh),
            OtherKwh = hours.Sum(h => h.OtherKwh),
            Cost = cost,
            ChargerCost = chargerCost,
            PricedKwh = pricedKwh,
            PricedChargerKwh = pricedChargerKwh,
            OtherCostParts = otherCost,
            PricedOtherKwh = pricedOtherKwh,
            HoursWithoutPrice = withoutPrice,
            ChargingSessions = sessions,
            Weather = SummarizeWeather(hours)
        };
    }

    /// <summary>Averages, extremes and sums over the hours that have weather; null when none do.</summary>
    internal static WeatherSummary? SummarizeWeather(IReadOnlyList<HourlyEnergy> hours)
    {
        // Each value is summarised over the hours where it is present, so a missing wind reading
        // does not drag the wind average towards zero.
        var weather = hours.Select(h => h.Weather).OfType<HourlyWeather>().ToList();
        if (weather.Count == 0)
            return null;

        var temps = weather.Select(w => w.Temperature).OfType<double>().ToList();
        return new WeatherSummary(
            weather.Count,
            temps.Count > 0 ? temps.Average() : null,
            temps.Count > 0 ? temps.Min() : null,
            temps.Count > 0 ? temps.Max() : null,
            Sum(weather.Select(w => w.Precipitation)),
            Average(weather.Select(w => w.WindSpeed)),
            Average(weather.Select(w => w.CloudCover)),
            temps.Count > 0 ? temps.Sum(t => Math.Max(0, WeatherSummary.DegreeDayBase - t)) / 24 : null);

        static double? Sum(IEnumerable<double?> values)
        {
            var list = values.OfType<double>().ToList();
            return list.Count > 0 ? list.Sum() : null;
        }

        static double? Average(IEnumerable<double?> values)
        {
            var list = values.OfType<double>().ToList();
            return list.Count > 0 ? list.Average() : null;
        }
    }
}
