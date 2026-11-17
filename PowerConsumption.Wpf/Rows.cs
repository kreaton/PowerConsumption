using System.Globalization;
using PowerConsumption.Data;

namespace PowerConsumption.Wpf;

/// <summary>
/// One line in the Table tab, formatted for display. Everything is a ready-made string so the
/// DataGrid needs no converters; the names match the column bindings in MainWindow.xaml.
/// "–" means "no price", an empty cell "not applicable" (e.g. no weather).
/// </summary>
public sealed record TableRow(
    string Period,
    string Kwh,
    string EvKwh,
    string OtherKwh,
    string Price,
    string Cost,
    string EvCost,
    string OtherCost,
    string OtherPrice,
    string Sessions,
    string AvgTemp,
    string MinTemp,
    string MaxTemp,
    string Precipitation,
    string DegreeDays,
    bool IsTotal,
    bool IsIncomplete)
{
    /// <summary>Formats a period. <paramref name="isTotal"/> makes the row bold and never pale (styles in MainWindow.xaml).</summary>
    /// <param name="p">The period.</param>
    /// <param name="period">Its display name.</param>
    /// <param name="includeVat">Amounts incl. or excl. VAT.</param>
    /// <param name="kwhFormat">kWh number format, by granularity.</param>
    /// <param name="isTotal">This is the total line.</param>
    public static TableRow From(EnergyPeriod p, string period, bool includeVat, string kwhFormat, bool isTotal)
    {
        var c = CultureInfo.CurrentCulture;
        var w = p.Weather;
        return new TableRow(
            period,
            p.HouseKwh.ToString(kwhFormat, c),
            p.ChargerKwh.ToString(kwhFormat, c),
            p.OtherKwh.ToString(kwhFormat, c),
            p.AveragePrice?.Pick(includeVat).ToString("N2", c) ?? "–",
            p.Cost?.Total.Pick(includeVat).ToString("N2", c) ?? "–",
            p.ChargerCost?.Pick(includeVat).ToString("N2", c) ?? (p.ChargerKwh > 0 ? "–" : ""),
            // The household without the EV, priced hour by hour.
            p.OtherCost?.Pick(includeVat).ToString("N2", c) ?? "–",
            p.OtherAveragePrice?.Pick(includeVat).ToString("N2", c) ?? "–",
            // Sessions start in one hour and run for many, so a per-hour count would mislead.
            p.Granularity == Granularity.Hour && !isTotal ? "" : p.ChargingSessions.ToString(c),
            w?.AverageTemperature?.ToString("N1", c) ?? "",
            w?.MinTemperature?.ToString("N1", c) ?? "",
            w?.MaxTemperature?.ToString("N1", c) ?? "",
            w?.Precipitation?.ToString("N1", c) ?? "",
            w?.HeatingDegreeDays?.ToString("N1", c) ?? "",
            isTotal,
            !isTotal && !p.IsComplete);
    }
}

/// <summary>One charging session in the EV tab, formatted for display (Danish time).</summary>
public sealed record SessionRow(string Start, string End, string Duration, string Kwh, string Cost, string Price, string Device)
{
    /// <summary>Formats a session; Price is its cost ÷ kWh, i.e. the average price paid while charging.</summary>
    public static SessionRow From(ChargingSession s, bool includeVat)
    {
        var c = CultureInfo.CurrentCulture;
        var duration = s.EndUtc - s.StartUtc;
        return new SessionRow(
            s.StartLocal.ToString("ddd d MMM yyyy HH:mm", c),
            s.EndLocal.ToString("ddd d MMM yyyy HH:mm", c),
            $"{(int)duration.TotalHours}:{duration.Minutes:00}",
            s.Kwh.ToString("N2", c),
            s.Cost?.Pick(includeVat).ToString("N2", c) ?? "–",
            s.Cost is { } cost && s.Kwh > 0 ? (cost.Pick(includeVat) / s.Kwh).ToString("N2", c) : "–",
            s.DeviceName);
    }
}
