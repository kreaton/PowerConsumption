using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using MySqlConnector;
using PowerConsumption.Data;
using PowerConsumption.Wpf.Controls;

namespace PowerConsumption.Wpf;

/// <summary>
/// The app's one window: a period navigator, five summary tiles and tabs with charts and tables.
/// <para>
/// State is simple: a <see cref="Granularity"/> and an anchor day decide the period shown
/// (see <see cref="EnergyService.RangeFor"/>), unless the user chose dates of their own under
/// Dates… (<see cref="_customRange"/>); every change reloads a report from the database, and <see cref="Render"/> redraws everything from that report. Toggling VAT only
/// re-renders, since every amount holds both values.
/// </para>
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Numbers and dates follow the user's Windows format (Danish decimal commas on a Danish PC).</summary>
    private static CultureInfo C => CultureInfo.CurrentCulture;

    // Colours shared with StromForbrug: household green, EV blue, and the price parts in the
    // order of PriceBreakdown.Components.
    private static readonly Color OtherColor = Color.FromRgb(27, 175, 122);
    private static readonly Color EvColor = Color.FromRgb(42, 120, 214);
    private static readonly Color LineColor = Color.FromRgb(235, 104, 52);
    private static readonly Color[] CostColors =
    [
        Color.FromRgb(42, 120, 214), Color.FromRgb(235, 104, 52), Color.FromRgb(27, 175, 122),
        Color.FromRgb(237, 161, 0), Color.FromRgb(232, 123, 164), Color.FromRgb(0, 131, 0)
    ];

    private readonly AppSettings _settings = AppSettings.Load();

    /// <summary>Null until connected (and again after a failed connection).</summary>
    private EnergyService? _service;

    /// <summary>What the database holds; gives the meter's first/last day for navigation.</summary>
    private DataCatalog? _catalog;

    /// <summary>The report on screen.</summary>
    private EnergyReport? _report;

    /// <summary>The bar size shown.</summary>
    private Granularity _granularity;

    /// <summary>A day inside the period shown; the granularity widens it to its day, month, year or all years.</summary>
    private DateOnly _anchor = DanishTime.Today;

    /// <summary>
    /// Dates the user chose under Dates… (to exclusive); null for the normal views around
    /// <see cref="_anchor"/>. The granularity then only decides the bar size inside the range.
    /// </summary>
    private (DateOnly From, DateOnly ToExclusive)? _customRange;

    /// <summary>Hour bars over more days than this would be too thin to read, so Hour is disabled for longer ranges.</summary>
    private const int MaxHourBarDays = 31;

    /// <summary>The dates shown: the chosen range, or the day/month/year/all years around the anchor.</summary>
    private (DateOnly From, DateOnly ToExclusive) CurrentRange =>
        _customRange ?? EnergyService.RangeFor(_granularity, _anchor, _catalog);

    /// <summary>The running load; cancelled when the user navigates on before it finishes.</summary>
    private CancellationTokenSource? _inFlight;

    /// <summary>True while code sets controls, so their change events do not trigger loads of their own.</summary>
    private bool _suppressEvents = true;

    public MainWindow()
    {
        InitializeComponent();
        Title = $"PowerConsumption {BuildInfo.Version} – electricity, EV charging and weather from electricity_db";

        // The charts' fixed set-up; their bars and lines are filled in by Render().
        KwhChart.Series = [new("Household", OtherColor), new("EV charging", EvColor)];
        KwhChart.Unit = "kWh";
        KwhChart.MutedLabel = "Incomplete";
        KwhChart.TooltipText = KwhTooltip;

        CostChart.Series = PriceBreakdown.Components.Select((c, i) => new ChartSeries(c.Label, CostColors[i])).ToArray();
        CostChart.MutedLabel = "Incomplete or missing price";
        CostChart.TooltipText = CostTooltip;

        EvChart.Series = [new("EV charging", EvColor)];
        EvChart.Unit = "kWh";
        EvChart.TooltipText = EvTooltip;
        EvChart.EmptyText = "No charging in this period.";

        ClimateChart.Series = [new("Household excl. EV", OtherColor)];
        ClimateChart.Unit = "kWh";
        ClimateChart.MutedLabel = "Incomplete";
        ClimateChart.TooltipText = ClimateTooltip;

        foreach (var chart in new[] { KwhChart, CostChart, EvChart, ClimateChart })
            chart.BarActivated += OnBarActivated;

        // Restore the last session's choices while events are still suppressed.
        VatCheck.IsChecked = _settings.IncludeVat;
        ExclEvCheck.IsChecked = _settings.ExcludeEv;
        SetGranularity(_settings.Granularity);
        _suppressEvents = false;

        // Connect once the window is on screen, so a settings dialog can open on top of it.
        Loaded += async (_, _) => await StartAsync();
        Closing += (_, _) => SaveSettings();
    }

    /// <summary>Show amounts incl. VAT (the checkbox).</summary>
    private bool IncludeVat => VatCheck.IsChecked == true;

    /// <summary>"incl. VAT" or "excl. VAT", for units and captions.</summary>
    private string VatText => IncludeVat ? "incl. VAT" : "excl. VAT";

    /// <summary>
    /// Leave the EV charger out (the checkbox): the consumption and cost charts and tiles then
    /// show the household alone. The EV and Climate tabs and the table are the same either way.
    /// </summary>
    private bool ExcludeEv => ExclEvCheck.IsChecked == true;

    /// <summary>The kWh shown for a period: the whole meter, or the household without the EV.</summary>
    private double ShownKwh(EnergyPeriod p) => ExcludeEv ? p.OtherKwh : p.HouseKwh;

    /// <summary>The cost shown, by price part: the whole meter's, or the household's without the EV.</summary>
    private PriceBreakdown? ShownCost(EnergyPeriod p) => ExcludeEv ? p.OtherCostParts : p.Cost;

    /// <summary>The average price of what is shown.</summary>
    private Money? ShownAveragePrice(EnergyPeriod p) => ExcludeEv ? p.OtherAveragePrice : p.AveragePrice;

    /// <summary>The kWh the shown cost was computed over.</summary>
    private double ShownPricedKwh(EnergyPeriod p) => ExcludeEv ? p.PricedOtherKwh : p.PricedKwh;

    // ---------------- connecting ----------------

    /// <summary>First start: ask for the password if there is none, then connect.</summary>
    private async Task StartAsync()
    {
        if (!_settings.HasConnection && !OpenSettings())
        {
            StatusText.Text = "Enter the database password under Settings.";
            SetupText.Text = "Not connected.";
            return;
        }
        await ConnectAsync();
    }

    /// <summary>Shows the settings dialog; true when the user pressed OK (the settings are then saved).</summary>
    private bool OpenSettings()
    {
        var dialog = new SettingsWindow(_settings) { Owner = this };
        return dialog.ShowDialog() == true;
    }

    /// <summary>Settings…: reconnect after OK, as the server or the meter may have changed.</summary>
    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (OpenSettings())
            await ConnectAsync();
    }

    /// <summary>About…: version, build time and credits.</summary>
    private void OnAboutClick(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

    /// <summary>
    /// (Re)connects with the current settings: reads the catalog, repairs choices that no longer
    /// exist in the database, jumps to the newest data and loads it.
    /// </summary>
    private async Task ConnectAsync()
    {
        SetBusy(true, $"Connecting to {_settings.Database} on {_settings.Server}…");
        try
        {
            _service = new EnergyService(_settings.ToDbSettings());
            _catalog = await _service.GetCatalogAsync();

            // A remembered meter, charger or location may have gone (or never been chosen):
            // fall back to the busiest meter, all chargers and the first weather location.
            // WeatherLocation "" means the user chose no weather, which is kept.

            if (_catalog.MeteringPoints.Count == 0)
            {
                ShowError("The database has no meter readings (eloverblik_energy_readings is empty).", null);
                return;
            }
            if (_catalog.MeteringPoints.All(m => m.Name != _settings.MeteringPointId))
                _settings.MeteringPointId = _catalog.MeteringPoints[0].Name;
            if (_settings.ChargerDevice is { } charger && _catalog.Chargers.All(c => c.Name != charger))
                _settings.ChargerDevice = null;
            if (_settings.WeatherLocation is null || (_settings.WeatherLocation != "" && _catalog.WeatherLocations.All(w => w.Name != _settings.WeatherLocation)))
                _settings.WeatherLocation = _catalog.WeatherLocations.FirstOrDefault()?.Name ?? "";

            _anchor = Meter!.LastDay;
            UpdateSetupText();
            await LoadAsync();
        }
        catch (Exception ex) when (ex is MySqlException or InvalidOperationException or TimeoutException)
        {
            _service = null;
            ShowError("Could not read electricity_db. Check the connection under Settings.", ex);
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    /// <summary>The chosen meter in the catalog, or null when not connected.</summary>
    private SourceRange? Meter => _catalog?.MeteringPoints.FirstOrDefault(m => m.Name == _settings.MeteringPointId);

    /// <summary>The data selection for queries; an empty weather location becomes "no weather".</summary>
    private EnergyFilter Filter => new(
        _settings.MeteringPointId!,
        _settings.ChargerDevice,
        string.IsNullOrEmpty(_settings.WeatherLocation) ? null : _settings.WeatherLocation);

    /// <summary>The line at the top: which database, meter, charger and weather location are shown.</summary>
    private void UpdateSetupText()
    {
        if (Meter is not { } meter)
        {
            SetupText.Text = "Not connected.";
            return;
        }
        var charger = _settings.ChargerDevice ?? (_catalog!.Chargers.Count switch { 0 => "none", 1 => _catalog.Chargers[0].Name, _ => "all" });
        var weather = string.IsNullOrEmpty(_settings.WeatherLocation) ? "none" : _settings.WeatherLocation;
        SetupText.Text = $"{_settings.Database} on {_settings.Server}  ·  meter {meter.Name} ({meter.FirstDay.ToString("d", C)} – {meter.LastDay.ToString("d", C)})" +
                         $"  ·  charger: {charger}  ·  weather: {weather}" +
                         (_catalog!.Prices is { } p ? $"  ·  prices until {p.LastDay.ToString("d", C)}" : "  ·  no prices");
    }

    // ---------------- navigation ----------------

    /// <summary>Sets the granularity and its radio button without the button's event starting a load.</summary>
    private void SetGranularity(Granularity granularity)
    {
        var suppress = _suppressEvents;
        _suppressEvents = true;
        _granularity = granularity;
        (granularity switch
        {
            Granularity.Hour => HourRadio,
            Granularity.Day => DayRadio,
            Granularity.Month => MonthRadio,
            _ => YearRadio
        }).IsChecked = true;
        _suppressEvents = suppress;
    }

    /// <summary>The user picked Hour/Day/Month/Year: same anchor day, new bar size.</summary>
    private async void OnGranularityChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;
        _granularity = sender == HourRadio ? Granularity.Hour
            : sender == DayRadio ? Granularity.Day
            : sender == MonthRadio ? Granularity.Month
            : Granularity.Year;
        await LoadAsync();
    }

    private async void OnPrevClick(object sender, RoutedEventArgs e) => await ShiftAsync(-1);
    private async void OnNextClick(object sender, RoutedEventArgs e) => await ShiftAsync(1);

    /// <summary>
    /// ◀ / ▶: one view back or forward (a day, month or year); the all-years view does not move.
    /// A chosen range moves by its own length: whole months by months (so March → April), else by days.
    /// </summary>
    private async Task ShiftAsync(int direction)
    {
        if (_customRange is var (from, to))
        {
            var months = (to.Year - from.Year) * 12 + to.Month - from.Month;
            _customRange = from.Day == 1 && to.Day == 1
                ? (from.AddMonths(direction * months), to.AddMonths(direction * months))
                : (from.AddDays(direction * (to.DayNumber - from.DayNumber)), to.AddDays(direction * (to.DayNumber - from.DayNumber)));
            await LoadAsync();
            return;
        }

        _anchor = _granularity switch
        {
            Granularity.Hour => _anchor.AddDays(direction),
            Granularity.Day => _anchor.AddMonths(direction),
            Granularity.Month => _anchor.AddYears(direction),
            _ => _anchor
        };
        await LoadAsync();
    }

    /// <summary>Jumps to the view holding the newest meter reading (leaving a chosen range).</summary>
    private async void OnLatestClick(object sender, RoutedEventArgs e)
    {
        _customRange = null;
        if (Meter is { } meter)
            _anchor = meter.LastDay;
        await LoadAsync();
    }

    /// <summary>
    /// Zoom out: one level coarser around the same anchor (a day's hours → its month's days, …).
    /// From a chosen range: the normal view with the same bars around the range's first day.
    /// </summary>
    private async void OnUpClick(object sender, RoutedEventArgs e)
    {
        if (_customRange is var (from, _))
        {
            _customRange = null;
            _anchor = from;
        }
        else if (_granularity == Granularity.Year)
            return;
        else
            SetGranularity(_granularity + 1);
        await LoadAsync();
    }

    /// <summary>Double-click on a bar: show that period one level finer (leaving a chosen range).</summary>
    private async void OnBarActivated(int index)
    {
        if (_report is null || index >= _report.Periods.Count || _granularity == Granularity.Hour)
            return;
        _customRange = null;
        _anchor = DateOnly.FromDateTime(_report.Periods[index].Start);
        SetGranularity(_granularity - 1);
        await LoadAsync();
    }

    /// <summary>
    /// Re-reads the database, e.g. after MultiApiFetcher has loaded new data: the catalog (so the
    /// newest day is updated) and the view on screen. Connects first if not connected.
    /// </summary>
    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (_service is null)
            await ConnectAsync();
        else
        {
            try
            {
                _catalog = await _service.GetCatalogAsync();
                UpdateSetupText();
            }
            catch (Exception ex) when (ex is MySqlException or InvalidOperationException)
            {
                ShowError("Could not read electricity_db.", ex);
                return;
            }
            await LoadAsync();
        }
    }

    // ---------------- chosen date range (Dates…) ----------------

    /// <summary>Opens the range popup on the dates shown now (to as the last day included).</summary>
    private void OnRangeClick(object sender, RoutedEventArgs e)
    {
        var (from, to) = CurrentRange;
        SetPickers(from, to.AddDays(-1));
        RangeError.Visibility = Visibility.Collapsed;
        RangePopup.IsOpen = true;
    }

    /// <summary>Last 7 days / 30 days / 12 months, ending with the newest day that has meter data.</summary>
    private void OnQuickRangeClick(object sender, RoutedEventArgs e)
    {
        var last = Meter?.LastDay ?? DanishTime.Today;
        var first = (string)((FrameworkElement)sender).Tag switch
        {
            "7d" => last.AddDays(-6),
            "30d" => last.AddDays(-29),
            // 12 whole months: from the 1st of the month eleven months back.
            _ => new DateOnly(last.Year, last.Month, 1).AddMonths(-11)
        };
        SetPickers(first, last);
    }

    private void SetPickers(DateOnly from, DateOnly lastDay)
    {
        FromPicker.SelectedDate = from.ToDateTime(TimeOnly.MinValue);
        ToPicker.SelectedDate = lastDay.ToDateTime(TimeOnly.MinValue);
    }

    /// <summary>Show: checks the dates, picks a bar size that suits the length and loads the range.</summary>
    private async void OnApplyRangeClick(object sender, RoutedEventArgs e)
    {
        if (FromPicker.SelectedDate is not { } fromDate || ToPicker.SelectedDate is not { } toDate)
        {
            ShowRangeError("Enter both dates.");
            return;
        }
        var from = DateOnly.FromDateTime(fromDate);
        var to = DateOnly.FromDateTime(toDate).AddDays(1);
        if (to <= from)
        {
            ShowRangeError("The last day must not be before the first.");
            return;
        }

        RangePopup.IsOpen = false;
        _customRange = (from, to);
        _anchor = from;
        SetGranularity(SuggestedGranularity(to.DayNumber - from.DayNumber));
        await LoadAsync();
    }

    private void OnCancelRangeClick(object sender, RoutedEventArgs e) => RangePopup.IsOpen = false;

    private void ShowRangeError(string message)
    {
        RangeError.Text = message;
        RangeError.Visibility = Visibility.Visible;
    }

    /// <summary>A bar size giving a readable number of bars: hours for up to 3 days, days up to about 4 months, months up to 5 years.</summary>
    private static Granularity SuggestedGranularity(int days) => days switch
    {
        <= 3 => Granularity.Hour,
        <= 124 => Granularity.Day,
        <= 5 * 366 => Granularity.Month,
        _ => Granularity.Year
    };

    /// <summary>VAT or EV on/off: every period carries all the values, so only a redraw is needed.</summary>
    private void OnVatChanged(object sender, RoutedEventArgs e)
    {
        if (!_suppressEvents)
            Render();
    }

    // ---------------- loading ----------------

    /// <summary>
    /// Loads the report for the current granularity and anchor and renders it. A newer call
    /// cancels an older one still running, so quick clicks on ◀ ▶ show only the last period.
    /// </summary>
    private async Task LoadAsync()
    {
        if (_service is null || _catalog is null || _settings.MeteringPointId is null)
            return;

        var (from, to) = CurrentRange;

        // Supersede any load still running.
        _inFlight?.Cancel();
        _inFlight?.Dispose();
        _inFlight = new CancellationTokenSource();
        var ct = _inFlight.Token;

        SetBusy(true, "Loading…");
        var watch = Stopwatch.StartNew();
        try
        {
            var report = await _service.GetReportAsync(Filter, _granularity, from, to, ct);
            // A query may finish after a newer load started; its result is then stale.
            if (ct.IsCancellationRequested)
                return;
            _report = report;
            Render();
            StatusText.Text = report.Hourly.Count == 0
                ? "No meter readings in this period."
                : $"{report.Hourly.Count:N0} metered hours, {report.Sessions.Count} charging sessions" +
                  (report.DataUntil is { } until ? $" – data until {until.ToString("ddd d MMM yyyy HH:mm", C)}" : "") +
                  $" – loaded in {watch.ElapsedMilliseconds} ms.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Superseded by a newer request.
        }
        catch (Exception ex) when (ex is MySqlException or InvalidOperationException or TimeoutException)
        {
            ShowError("Could not load the data.", ex);
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                SetBusy(false, null);
        }
    }

    // ---------------- rendering ----------------

    /// <summary>The periods on screen, one per bar and table row (empty before the first load).</summary>
    private IReadOnlyList<EnergyPeriod> Periods => _report?.Periods ?? [];

    /// <summary>More than 1 % of the hours lack a price: worth drawing pale (one hour in a year is not).</summary>
    private static bool MissesPrices(EnergyPeriod p) => p.HoursWithoutPrice > 0 && p.HoursWithoutPrice * 100 > p.Hours;

    /// <summary>
    /// Draws the current report everywhere: the navigation, the four charts, the tiles, the table
    /// and the session list. Bar <c>i</c> in every chart is <c>Periods[i]</c>, which the tooltips
    /// and drill-down rely on.
    /// </summary>
    private void Render()
    {
        UpdateNavigation();
        if (_report is not { } report)
            return;

        var periods = report.Periods;
        // x-axis label per bar, and a thin line before each Monday in the day view.
        var bars = periods.Select(p => (Label: BarLabel(p), Separator: _granularity == Granularity.Day && p.Start.DayOfWeek == DayOfWeek.Monday)).ToList();

        // Consumption: household and EV stacked (or the household alone); pale where hours are missing.
        KwhChart.ValueFormat = KwhFormat;
        KwhChart.Unit = ExcludeEv ? "kWh excl. EV" : "kWh";
        KwhChart.Series = ExcludeEv ? [new("Household excl. EV", OtherColor)] : [new("Household", OtherColor), new("EV charging", EvColor)];
        KwhChart.Bars = periods.Select((p, i) => new ChartBar(ExcludeEv ? [p.OtherKwh] : [p.OtherKwh, p.ChargerKwh], bars[i].Label, !p.IsComplete, bars[i].Separator)).ToList();

        // Cost: one stacked segment per price part. Empty when the whole view has no prices;
        // a single unpriced period gets an empty (all-zero) bar so the bars stay aligned.
        CostChart.Unit = "kr " + VatText + (ExcludeEv ? ", excl. EV" : "");
        CostChart.ValueFormat = "N2";
        CostChart.EmptyText = "No prices for this period.";
        CostChart.Bars = ShownCost(report.Total) is null
            ? []
            : periods.Select((p, i) => new ChartBar(
                ShownCost(p) is { } c ? PriceBreakdown.Components.Select(part => part.Get(c).Pick(IncludeVat)).ToArray() : new double[PriceBreakdown.Components.Length],
                bars[i].Label, !p.IsComplete || MissesPrices(p), bars[i].Separator)).ToList();

        // EV: charged kWh, with the average price paid per kWh as a line on its own axis.
        EvChart.ValueFormat = KwhFormat;
        EvChart.Bars = report.Total.ChargerKwh > 0
            ? periods.Select((p, i) => new ChartBar([p.ChargerKwh], bars[i].Label, false, bars[i].Separator)).ToList()
            : [];
        EvChart.Line = report.Total.ChargerKwh > 0
            ? new ChartLine(new ChartSeries("Price paid", LineColor), periods.Select(p => p.ChargerAveragePrice?.Pick(IncludeVat)).ToArray(), "kr/kWh")
            : null;

        // Climate: the household without the EV (the part the weather can affect) against temperature.
        ClimateChart.ValueFormat = KwhFormat;
        ClimateChart.EmptyText = "No data.";
        ClimateChart.Bars = periods.Select((p, i) => new ChartBar([p.OtherKwh], bars[i].Label, !p.IsComplete, bars[i].Separator)).ToList();
        ClimateChart.Line = report.Total.Weather is null
            ? null
            : new ChartLine(new ChartSeries("Average temperature", LineColor), periods.Select(p => p.Weather?.AverageTemperature).ToArray(), "°C");

        RenderTiles(report.Total);
        // Table: one row per period plus a bold total; sessions newest first.
        TableGrid.ItemsSource = periods.Select(p => TableRow.From(p, Head(p, withCoverage: false), IncludeVat, KwhFormat, isTotal: false))
            .Append(TableRow.From(report.Total, "Total", IncludeVat, KwhFormat, isTotal: true))
            .ToList();
        SessionGrid.ItemsSource = report.Sessions.Select(s => SessionRow.From(s, IncludeVat)).Reverse().ToList();
        ExportButton.IsEnabled = periods.Count > 0;
    }

    /// <summary>The five summary tiles, from the report's total over the whole view.</summary>
    private void RenderTiles(EnergyPeriod total)
    {
        var kwh = ShownKwh(total);
        KwhCaption.Text = ExcludeEv ? "Consumption excl. EV" : "Consumption";
        KwhValue.Text = $"{kwh.ToString("N1", C)} kWh";
        KwhSub.Text = (ExcludeEv
                          ? $"whole meter {total.HouseKwh.ToString("N1", C)} · EV {total.ChargerKwh.ToString("N1", C)} kWh"
                          : $"household {total.OtherKwh.ToString("N1", C)} · EV {total.ChargerKwh.ToString("N1", C)} kWh") +
                      (total.Hours > 0 && _granularity != Granularity.Hour ? $" · {(kwh / total.Hours * 24).ToString("N1", C)} kWh/day" : "");

        CostCaption.Text = ExcludeEv ? "Cost excl. EV" : "Cost";
        AvgCaption.Text = ExcludeEv ? "Average price excl. EV" : "Average price";
        if (ShownCost(total) is { } cost)
        {
            CostValue.Text = $"{cost.Total.Pick(IncludeVat).ToString("N2", C)} kr";
            CostSub.Text = VatText +
                           (ExcludeEv
                               ? total.Cost is { } all ? $" · whole meter {all.Total.Pick(IncludeVat).ToString("N0", C)} kr" : ""
                               : total.OtherCost is { } other ? $" · household {other.Pick(IncludeVat).ToString("N0", C)} kr" : "") +
                           (total.HoursWithoutPrice > 0 ? $" · {total.HoursWithoutPrice} h without price" : "");
            AvgValue.Text = ShownAveragePrice(total) is { } avg ? $"{avg.Pick(IncludeVat).ToString("N2", C)} kr/kWh" : "–";
            AvgSub.Text = "weighted by consumption, " + VatText +
                          (ExcludeEv && total.AveragePrice is { } allAvg ? $" · whole meter {allAvg.Pick(IncludeVat).ToString("N2", C)}" : "");
        }
        else
        {
            CostValue.Text = AvgValue.Text = "–";
            CostSub.Text = "no prices";
            AvgSub.Text = "";
        }

        EvValue.Text = $"{total.ChargerKwh.ToString("N1", C)} kWh";
        EvSub.Text = $"{total.ChargingSessions} sessions" +
                     (total.ChargerCost is { } ec ? $" · {ec.Pick(IncludeVat).ToString("N0", C)} kr" : "") +
                     (total.ChargerAveragePrice is { } ep ? $" · {ep.Pick(IncludeVat).ToString("N2", C)} kr/kWh" : "") +
                     (total.HouseKwh > 0 ? $" · {(total.ChargerKwh / total.HouseKwh).ToString("P0", C)} of all" : "");

        WeatherCaption.Text = string.IsNullOrEmpty(_settings.WeatherLocation) ? "Weather" : $"Weather · {_settings.WeatherLocation}";
        if (total.Weather is { AverageTemperature: { } t } w)
        {
            WeatherValue.Text = $"{t.ToString("N1", C)} °C";
            WeatherSub.Text = $"{w.MinTemperature?.ToString("N1", C)} to {w.MaxTemperature?.ToString("N1", C)} °C" +
                              (w.Precipitation is { } rain ? $" · {rain.ToString("N0", C)} mm" : "") +
                              (w.HeatingDegreeDays is { } hdd ? $" · {hdd.ToString("N0", C)} degree days" : "") +
                              (w.Hours < total.Hours ? $" · {w.Hours} of {total.Hours} h" : "");
        }
        else
        {
            WeatherValue.Text = "–";
            WeatherSub.Text = string.IsNullOrEmpty(_settings.WeatherLocation) ? "no location chosen" : "no weather for this period";
        }
    }

    /// <summary>
    /// The period title and the navigation buttons. ◀ ▶ are disabled at the ends of the meter
    /// data, so the user cannot wander into empty years.
    /// </summary>
    private void UpdateNavigation()
    {
        var (from, to) = CurrentRange;
        var custom = _customRange is not null;
        PeriodText.Text = custom
            ? (to.DayNumber - from.DayNumber == 1
                ? from.ToString("dddd d MMMM yyyy", C)
                : $"{from.ToString("d MMM yyyy", C)} – {to.AddDays(-1).ToString("d MMM yyyy", C)}")
            : _granularity switch
            {
                Granularity.Hour => _anchor.ToString("dddd d MMMM yyyy", C),
                Granularity.Day => new DateTime(from.Year, from.Month, 1).ToString("MMMM yyyy", C),
                Granularity.Month => from.Year.ToString(C),
                _ => $"{from.Year}–{to.Year - 1}"
            };
        var meter = Meter;
        // A chosen range can always move; the all-years view cannot.
        var movable = custom || _granularity != Granularity.Year;
        PrevButton.IsEnabled = movable && meter is not null && from > meter.FirstDay;
        NextButton.IsEnabled = movable && meter is not null && to <= meter.LastDay;
        UpButton.IsEnabled = custom || _granularity != Granularity.Year;
        HourRadio.IsEnabled = !custom || to.DayNumber - from.DayNumber <= MaxHourBarDays;
        HourRadio.ToolTip = HourRadio.IsEnabled ? null : $"Hour bars are available for up to {MaxHourBarDays} days.";
    }

    /// <summary>kWh decimals by bar size: an hour is a fraction of a kWh, a year thousands.</summary>
    private string KwhFormat => _granularity switch
    {
        Granularity.Hour => "N2",
        Granularity.Day => "N1",
        _ => "N0"
    };

    /// <summary>
    /// The short x-axis label of a bar: hour, day of month, month name or year. When a chosen
    /// range spans several days, months or years, the label adds the day, month or year so it stays unambiguous.
    /// </summary>
    private string BarLabel(EnergyPeriod p)
    {
        var (from, to) = CurrentRange;
        var last = to.AddDays(-1);
        return _granularity switch
        {
            Granularity.Hour => from == last ? p.Start.ToString("HH", C) : p.Start.ToString("ddd HH", C),
            Granularity.Day => (from.Year, from.Month) == (last.Year, last.Month) ? p.Start.Day.ToString(C) : p.Start.ToString("d MMM", C),
            Granularity.Month => from.Year == last.Year ? p.Start.ToString("MMM", C) : p.Start.ToString("MMM yy", C),
            _ => p.Start.Year.ToString(C)
        };
    }

    /// <summary>
    /// The full name of a period for tooltips and the table, e.g. "Mon 9 Nov 2026" or
    /// "November 2026"; with <paramref name="withCoverage"/>, incomplete periods say how many hours they have.
    /// </summary>
    private string Head(EnergyPeriod p, bool withCoverage = true)
    {
        var head = _granularity switch
        {
            Granularity.Hour => $"{p.Start.ToString("ddd d MMM", C)} {p.Start.ToString("HH:mm", C)}–{p.End.ToString("HH:mm", C)}",
            Granularity.Day => p.Start.ToString("ddd d MMM yyyy", C),
            Granularity.Month => p.Start.ToString("MMMM yyyy", C),
            _ => p.Start.Year.ToString(C)
        };
        if (withCoverage && !p.IsComplete && _granularity != Granularity.Hour)
            head += $"  ({p.Hours} of {p.ExpectedHours} hours metered)";
        return head;
    }

    // ---------------- tooltips ----------------
    // Each chart asks for the text of the bar under the mouse by index. The tooltip uses a
    // monospaced font, so the labels are padded and numbers right-aligned to line up in columns.

    /// <summary>The period behind bar <paramref name="index"/>, or null if the bars changed meanwhile.</summary>
    private EnergyPeriod? At(int index) => index < Periods.Count ? Periods[index] : null;

    /// <summary>kWh right-aligned in 9 characters.</summary>
    private string Kwh(double v) => $"{v.ToString(KwhFormat, C),9} kWh";

    /// <summary>Kroner (incl. or excl. VAT as chosen) right-aligned in 9 characters.</summary>
    private string Kr(Money m) => $"{m.Pick(IncludeVat).ToString("N2", C),9} kr";

    private string KrPerKwh(Money m) => $"{m.Pick(IncludeVat).ToString("N2", C)} kr/kWh";

    /// <summary>Consumption tab: total, household and EV kWh, and the cost.</summary>
    private string KwhTooltip(int index)
    {
        if (At(index) is not { } p)
            return "";
        var sb = new StringBuilder().AppendLine(Head(p));
        sb.AppendLine($"Total      {Kwh(p.HouseKwh)}");
        sb.AppendLine($"Household  {Kwh(p.OtherKwh)}");
        sb.AppendLine($"EV         {Kwh(p.ChargerKwh)}");
        if (p.Cost is { } c)
            sb.AppendLine($"Cost       {Kr(c.Total)} ({KrPerKwh(p.AveragePrice!.Value)}, {VatText})");
        if (p.Granularity != Granularity.Hour)
            sb.Append("Double-click to zoom in.");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Cost tab: every price part, VAT, the EV's share and the average price.</summary>
    private string CostTooltip(int index)
    {
        if (At(index) is not { } p)
            return "";
        var sb = new StringBuilder().AppendLine(Head(p) + (ExcludeEv ? " · household excl. EV" : ""));
        if (ShownCost(p) is not { } c)
            return sb.Append("No price for this period.").ToString();

        // Top part first, as stacked in the bar.
        for (var i = PriceBreakdown.Components.Length - 1; i >= 0; i--)
            sb.AppendLine($"{PriceBreakdown.Components[i].Label,-16}{Kr(PriceBreakdown.Components[i].Get(c))}");
        sb.AppendLine($"{"Total excl. VAT",-16}{c.Total.ExVat.ToString("N2", C),9} kr");
        sb.AppendLine($"{"VAT",-16}{c.Total.Vat.ToString("N2", C),9} kr");
        sb.AppendLine($"{"Total incl. VAT",-16}{c.Total.Total.ToString("N2", C),9} kr");
        if (p.ChargerCost is { } ec)
            sb.AppendLine($"{(ExcludeEv ? "EV, left out" : "of which EV"),-16}{Kr(ec)}");
        sb.Append($"{ShownPricedKwh(p).ToString(KwhFormat, C)} kWh at {(ShownAveragePrice(p) is { } avg ? KrPerKwh(avg) : "–")}");
        if (p.HoursWithoutPrice > 0)
            sb.Append($" · {p.HoursWithoutPrice} hours without price");
        return sb.ToString();
    }

    /// <summary>EV tab: charged kWh and their cost, compared with the household's average price.</summary>
    private string EvTooltip(int index)
    {
        if (At(index) is not { } p)
            return "";
        var sb = new StringBuilder().AppendLine(Head(p));
        sb.AppendLine($"Charged    {Kwh(p.ChargerKwh)}");
        if (p.ChargerCost is { } ec)
            sb.AppendLine($"Cost       {Kr(ec)} ({KrPerKwh(p.ChargerAveragePrice!.Value)})");
        if (p.AveragePrice is { } avg)
            sb.AppendLine($"Household avg price: {KrPerKwh(avg)}");
        if (p.Granularity != Granularity.Hour)
            sb.AppendLine($"Sessions started: {p.ChargingSessions}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Climate tab: household kWh and the weather; min/max and degree days only make sense above the hour.</summary>
    private string ClimateTooltip(int index)
    {
        if (At(index) is not { } p)
            return "";
        var sb = new StringBuilder().AppendLine(Head(p));
        sb.AppendLine($"Household excl. EV {Kwh(p.OtherKwh)}");
        if (p.Weather is not { } w)
            return sb.Append("No weather data.").ToString();
        if (w.AverageTemperature is { } t)
            sb.AppendLine(p.Granularity == Granularity.Hour
                ? $"Temperature  {t.ToString("N1", C)} °C"
                : $"Temperature  {t.ToString("N1", C)} °C (min {w.MinTemperature?.ToString("N1", C)}, max {w.MaxTemperature?.ToString("N1", C)})");
        if (w.HeatingDegreeDays is { } hdd && p.Granularity != Granularity.Hour)
            sb.AppendLine($"Degree days  {hdd.ToString("N1", C)} (base {WeatherSummary.DegreeDayBase:0} °C)");
        if (w.Precipitation is { } rain)
            sb.AppendLine($"Rain         {rain.ToString("N1", C)} mm");
        if (w.AverageWindSpeed is { } wind)
            sb.AppendLine($"Wind         {wind.ToString("N1", C)} km/h");
        if (w.AverageCloudCover is { } cloud)
            sb.AppendLine($"Cloud cover  {cloud.ToString("N0", C)} %");
        return sb.ToString().TrimEnd();
    }

    // ---------------- export ----------------

    /// <summary>Saves the periods on screen as CSV; the folder is remembered for next time.</summary>
    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_report is not { } report || report.Periods.Count == 0)
            return;

        var dialog = new SaveFileDialog
        {
            Title = "Export to CSV",
            Filter = "CSV file (*.csv)|*.csv",
            FileName = $"powerconsumption-{report.From:yyyy-MM-dd}-{report.ToExclusive.AddDays(-1):yyyy-MM-dd}-{_granularity.ToString().ToLowerInvariant()}",
            InitialDirectory = _settings.ExportFolder ?? "",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            await CsvExport.SaveAsync(report.Periods, dialog.FileName, C);
            _settings.ExportFolder = Path.GetDirectoryName(dialog.FileName);
            StatusText.Text = $"Saved {dialog.FileName}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("Could not save the file.", ex);
        }
    }

    // ---------------- helpers ----------------

    /// <summary>
    /// Busy cursor while loading. Navigation stays enabled on purpose: a new click cancels the
    /// running load. <paramref name="message"/>, when given, goes to the status bar.
    /// </summary>
    private void SetBusy(bool busy, string? message)
    {
        Cursor = busy ? System.Windows.Input.Cursors.AppStarting : null;
        RefreshButton.IsEnabled = !busy;
        if (message is not null)
            StatusText.Text = message;
    }

    /// <summary>Puts <paramref name="context"/> in the status bar and shows it, with the exception's message, in a dialog.</summary>
    private void ShowError(string context, Exception? ex)
    {
        StatusText.Text = context;
        MessageBox.Show(this, ex is null ? context : $"{context}{Environment.NewLine}{Environment.NewLine}{ex.Message}",
            "PowerConsumption", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>Remembers the VAT and EV choices and bar size for the next start (the rest is saved by the settings dialog).</summary>
    private void SaveSettings()
    {
        _settings.IncludeVat = IncludeVat;
        _settings.ExcludeEv = ExcludeEv;
        _settings.Granularity = _granularity;
        _settings.Save();
    }

    /// <summary>Stops a load still running when the window closes.</summary>
    protected override void OnClosed(EventArgs e)
    {
        _inFlight?.Cancel();
        _inFlight?.Dispose();
        base.OnClosed(e);
    }
}
