using System.Globalization;
using System.IO;
using System.Text;
using PowerConsumption.Data;

namespace PowerConsumption.Wpf;

/// <summary>
/// Writes periods as CSV: a header line, then one line per period. Times are Danish wall-clock
/// time; cost parts are excl. VAT, the totals both with and without; household_* is the
/// consumption without the EV charger. With da-DK the separator is
/// ";" and decimals use commas, so the file opens straight into columns in a Danish Excel.
/// </summary>
public static class CsvExport
{
    // Column names in English snake_case so the file is easy to load in scripts; the order must
    // match the values written in Write().
    private static readonly string[] Header =
    [
        "start", "end", "hours", "kwh", "ev_kwh", "other_kwh",
        "spot_kr", "surcharge_kr", "net_tariff_kr", "system_tariff_kr", "distribution_kr", "electricity_tax_kr",
        "total_ex_vat_kr", "vat_kr", "total_kr", "price_kr_per_kwh", "ev_total_kr",
        "household_total_kr", "household_price_kr_per_kwh", "charging_sessions",
        "avg_temp_c", "min_temp_c", "max_temp_c", "precipitation_mm", "avg_wind_kmh", "heating_degree_days"
    ];

    /// <summary>Writes the header and one line per period; missing values are empty fields.</summary>
    /// <param name="periods">The periods, one line each, in the given order.</param>
    /// <param name="writer">Where to write; not closed.</param>
    /// <param name="culture">Decides the separator (its list separator) and the decimal mark.</param>
    public static void Write(IEnumerable<EnergyPeriod> periods, TextWriter writer, CultureInfo culture)
    {
        var sep = culture.TextInfo.ListSeparator;
        writer.WriteLine(string.Join(sep, Header));
        foreach (var p in periods)
        {
            var c = p.Cost;
            var w = p.Weather;
            writer.WriteLine(string.Join(sep,
                Time(p.Start), Time(p.End), p.Hours.ToString(CultureInfo.InvariantCulture),
                N(p.HouseKwh), N(p.ChargerKwh), N(p.OtherKwh),
                N(c?.Spot.ExVat), N(c?.Surcharge.ExVat), N(c?.NetTariff.ExVat), N(c?.SystemTariff.ExVat),
                N(c?.Distribution.ExVat), N(c?.ElectricityTax.ExVat),
                N(c?.Total.ExVat), N(c?.Total.Vat), N(c?.Total.Total), N(p.AveragePrice?.Total),
                N(p.ChargerCost?.Total), N(p.OtherCost?.Total), N(p.OtherAveragePrice?.Total), p.ChargingSessions.ToString(CultureInfo.InvariantCulture),
                N(w?.AverageTemperature), N(w?.MinTemperature), N(w?.MaxTemperature), N(w?.Precipitation),
                N(w?.AverageWindSpeed), N(w?.HeatingDegreeDays)));
        }

        // Full precision without thousands separators (they would split fields in some cultures).
        string N(double? value) => value?.ToString("0.######", culture) ?? "";
    }

    /// <summary>Writes the CSV to a file as UTF-8 without BOM.</summary>
    public static async Task SaveAsync(IEnumerable<EnergyPeriod> periods, string path, CultureInfo culture)
    {
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        Write(periods, writer, culture);
    }

    /// <summary>ISO-like local time, culture-independent so it sorts and parses everywhere.</summary>
    private static string Time(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
}
