namespace PowerConsumption.Data;

/// <summary>
/// An amount in kroner (or kroner per kWh) without VAT, and its VAT. Keeping both lets the app
/// switch between "incl." and "excl. VAT" without reloading.
/// </summary>
/// <param name="ExVat">The amount excl. VAT.</param>
/// <param name="Vat">The VAT on it.</param>
public readonly record struct Money(double ExVat, double Vat)
{
    /// <summary>The amount incl. VAT.</summary>
    public double Total => ExVat + Vat;

    /// <summary>Multiplies both parts, e.g. a price per kWh by kWh to get a cost.</summary>
    public Money Scale(double factor) => new(ExVat * factor, Vat * factor);

    /// <summary><see cref="Total"/> or <see cref="ExVat"/>, as the user chose to see it.</summary>
    public double Pick(bool includeVat) => includeVat ? Total : ExVat;

    public static Money operator +(Money a, Money b) => new(a.ExVat + b.ExVat, a.Vat + b.Vat);

    public static readonly Money Zero = new(0, 0);
}

/// <summary>
/// A price or a cost split into the parts Strømligning reports. <see cref="Total"/> is
/// Strømligning's own total ("price"); the parts are stored with two decimals, so their sum
/// can be off from it by an øre or so per kWh. The same type holds a price per kWh (one hour)
/// and a cost (a price scaled by kWh and summed over hours).
/// </summary>
/// <param name="Spot">The spot price ("electricity" in Strømligning).</param>
/// <param name="Surcharge">The electricity supplier's surcharge.</param>
/// <param name="NetTariff">Energinet's net tariff.</param>
/// <param name="SystemTariff">Energinet's system tariff.</param>
/// <param name="Distribution">The grid company's distribution tariff (varies by time of day).</param>
/// <param name="ElectricityTax">Elafgift, the state electricity tax.</param>
/// <param name="Total">Strømligning's own total ("price").</param>
public readonly record struct PriceBreakdown(
    Money Spot,
    Money Surcharge,
    Money NetTariff,
    Money SystemTariff,
    Money Distribution,
    Money ElectricityTax,
    Money Total)
{
    /// <summary>Multiplies every part, e.g. an hour's price by that hour's kWh.</summary>
    public PriceBreakdown Scale(double factor) => new(
        Spot.Scale(factor), Surcharge.Scale(factor), NetTariff.Scale(factor), SystemTariff.Scale(factor),
        Distribution.Scale(factor), ElectricityTax.Scale(factor), Total.Scale(factor));

    public static PriceBreakdown operator +(PriceBreakdown a, PriceBreakdown b) => new(
        a.Spot + b.Spot, a.Surcharge + b.Surcharge, a.NetTariff + b.NetTariff, a.SystemTariff + b.SystemTariff,
        a.Distribution + b.Distribution, a.ElectricityTax + b.ElectricityTax, a.Total + b.Total);

    /// <summary>All parts zero; the starting point when summing costs.</summary>
    public static readonly PriceBreakdown Zero = default;

    /// <summary>The parts in display order (bottom of a stacked bar first).</summary>
    public static readonly (string Label, Func<PriceBreakdown, Money> Get)[] Components =
    [
        ("Spot price", p => p.Spot),
        ("Surcharge", p => p.Surcharge),
        ("Net tariff", p => p.NetTariff),
        ("System tariff", p => p.SystemTariff),
        ("Distribution", p => p.Distribution),
        ("Electricity tax", p => p.ElectricityTax)
    ];
}
