using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using MySqlConnector;
using PowerConsumption.Data;

namespace PowerConsumption.Wpf;

/// <summary>
/// Database connection, and which meter, charger and weather location to show. The choices are
/// filled from the database itself (<see cref="EnergyService.GetCatalogAsync"/>) when connected.
/// The settings object is changed and saved only on OK.
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>A combo box entry: the text shown, and the value stored in the settings.</summary>
    private sealed record Option(string Display, string? Value);

    private readonly AppSettings _settings;

    /// <summary>Opens on <paramref name="settings"/>; connects at once when a password is known, to fill the lists.</summary>
    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        ServerBox.Text = settings.Server;
        PortBox.Text = settings.Port.ToString(CultureInfo.InvariantCulture);
        DatabaseBox.Text = settings.Database;
        UserBox.Text = settings.User;
        // The password itself is never shown; the hint says where it will come from if left empty.
        PasswordHint.Text = settings.Password is { Length: > 0 }
            ? "A password is saved (encrypted for your Windows user). Leave empty to keep it."
            : Environment.GetEnvironmentVariable(AppSettings.PasswordEnvironmentVariable) is { Length: > 0 }
                ? $"Leave empty to use the {AppSettings.PasswordEnvironmentVariable} environment variable."
                : "Saved encrypted for your Windows user.";

        // Until connected, keep the current choices so OK does not lose them.
        Fill(MeterCombo, settings.MeteringPointId is { } m ? [new(m, m)] : [], settings.MeteringPointId);
        Fill(ChargerCombo, [new("All chargers", null), .. settings.ChargerDevice is { } c ? new[] { new Option(c, c) } : []], settings.ChargerDevice);
        Fill(WeatherCombo, [new("No weather", ""), .. settings.WeatherLocation is { Length: > 0 } w ? new[] { new Option(w, w) } : []], settings.WeatherLocation ?? "");

        Loaded += async (_, _) =>
        {
            if (settings.HasConnection)
                await ConnectAsync();
            else
                PasswordBox.Focus();
        };
    }

    /// <summary>
    /// The connection as typed; an empty password box means the saved password (or the
    /// environment variable). Null, with a message, when the port is invalid.
    /// </summary>
    private DbSettings? ReadDbSettings()
    {
        if (!int.TryParse(PortBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            ConnectStatus.Text = "The port must be a number between 1 and 65535.";
            return null;
        }
        var password = PasswordBox.Password is { Length: > 0 } typed ? typed : _settings.EffectivePassword;
        return new DbSettings(ServerBox.Text.Trim(), port, DatabaseBox.Text.Trim(), UserBox.Text.Trim(), password);
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e) => await ConnectAsync();

    /// <summary>
    /// Tests the connection by reading the catalog, and fills the three lists from it, keeping the
    /// current choice where it still exists. Errors are shown next to the button, not as dialogs.
    /// </summary>
    private async Task ConnectAsync()
    {
        if (ReadDbSettings() is not { } db)
            return;

        ConnectButton.IsEnabled = false;
        ConnectStatus.Text = "Connecting…";
        try
        {
            var catalog = await new EnergyService(db).GetCatalogAsync();
            var c = CultureInfo.CurrentCulture;
            string Span(SourceRange r) => $"{r.FirstDay.ToString("d", c)} – {r.LastDay.ToString("d", c)}";

            Fill(MeterCombo,
                catalog.MeteringPoints.Select(m => new Option($"{m.Name}  ({Span(m)}, {m.Count:N0} hours)", m.Name)).ToList(),
                Current(MeterCombo) ?? catalog.MeteringPoints.FirstOrDefault()?.Name);
            Fill(ChargerCombo,
                [new("All chargers", null), .. catalog.Chargers.Select(ch => new Option($"{ch.Name}  ({Span(ch)}, {ch.Count:N0} sessions)", ch.Name))],
                Current(ChargerCombo));
            Fill(WeatherCombo,
                [new("No weather", ""), .. catalog.WeatherLocations.Select(w => new Option($"{w.Name}  ({Span(w)})", w.Name))],
                Current(WeatherCombo) is { Length: > 0 } loc ? loc : catalog.WeatherLocations.FirstOrDefault()?.Name ?? "");

            ConnectStatus.Text = catalog.MeteringPoints.Count == 0 ? "Connected, but there are no meter readings." : "Connected.";
        }
        catch (Exception ex) when (ex is MySqlException or InvalidOperationException or TimeoutException)
        {
            ConnectStatus.Text = ex.Message;
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    /// <summary>The stored value of the selected entry.</summary>
    private static string? Current(ComboBox combo) => (combo.SelectedItem as Option)?.Value;

    /// <summary>Replaces a list's entries and selects <paramref name="selected"/>, or the first entry if it is not there.</summary>
    private static void Fill(ComboBox combo, IReadOnlyList<Option> options, string? selected)
    {
        combo.ItemsSource = options;
        combo.SelectedItem = options.FirstOrDefault(o => o.Value == selected) ?? options.FirstOrDefault();
    }

    /// <summary>
    /// Stores everything and closes with OK. Refuses to close while there is no password at all,
    /// since the app could not connect.
    /// </summary>
    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (ReadDbSettings() is not { } db)
            return;

        _settings.Server = db.Server;
        _settings.Port = db.Port;
        _settings.Database = db.Database;
        _settings.User = db.User;
        if (PasswordBox.Password is { Length: > 0 } password)
            _settings.Password = password;

        if (!_settings.HasConnection)
        {
            ConnectStatus.Text = "Enter the database password.";
            PasswordBox.Focus();
            return;
        }

        _settings.MeteringPointId = Current(MeterCombo) ?? _settings.MeteringPointId;
        _settings.ChargerDevice = Current(ChargerCombo);
        _settings.WeatherLocation = Current(WeatherCombo) ?? "";
        _settings.Save();
        DialogResult = true;
    }
}
