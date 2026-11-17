using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PowerConsumption.Data;

namespace PowerConsumption.Wpf;

/// <summary>
/// The user's settings, kept in %APPDATA%\PowerConsumption\settings.json. The database password
/// is encrypted with DPAPI for this Windows user. Without a stored password the app uses the
/// environment variable MySqlConnection__Password, the same one MultiApiFetcher reads.
/// </summary>
public sealed class AppSettings
{
    /// <summary>The variable MultiApiFetcher reads its database password from (the .NET configuration form of MySqlConnection:Password).</summary>
    public const string PasswordEnvironmentVariable = "MySqlConnection__Password";

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PowerConsumption", "settings.json");

    // Enums as names ("Day"), so the file is readable and survives reordering the enum.
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    // Extra DPAPI input: other programs running as the same user cannot decrypt the password
    // without also knowing this value.
    private static readonly byte[] Entropy = "PowerConsumption.electricity_db"u8.ToArray();

    // Connection; the defaults are MultiApiFetcher's.
    public string Server { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 3306;
    public string Database { get; set; } = "electricity_db";
    public string User { get; set; } = "perh";

    /// <summary>DPAPI-protected password, base64.</summary>
    public string? ProtectedPassword { get; set; }

    /// <summary>The meter to show; null until the first connection picks one.</summary>
    public string? MeteringPointId { get; set; }

    /// <summary>Null: all chargers.</summary>
    public string? ChargerDevice { get; set; }

    /// <summary>Null: the first location in the database. Empty: no weather.</summary>
    public string? WeatherLocation { get; set; }

    // Remembered between runs from the main window.
    public bool IncludeVat { get; set; } = true;
    public bool ExcludeEv { get; set; }
    public Granularity Granularity { get; set; } = Granularity.Day;
    public string? ExportFolder { get; set; }

    /// <summary>The stored password in plain text (decrypted on read, encrypted on write); not written to the file as such.</summary>
    [JsonIgnore]
    public string? Password
    {
        get => Unprotect(ProtectedPassword);
        set => ProtectedPassword = Protect(value);
    }

    /// <summary>The stored password, or else the environment variable.</summary>
    [JsonIgnore]
    public string? EffectivePassword =>
        Password is { Length: > 0 } p ? p : Environment.GetEnvironmentVariable(PasswordEnvironmentVariable);

    /// <summary>Enough to try connecting: a server and some password.</summary>
    [JsonIgnore]
    public bool HasConnection => !string.IsNullOrWhiteSpace(Server) && !string.IsNullOrWhiteSpace(EffectivePassword);

    /// <summary>The connection for the data module; <paramref name="password"/> overrides the stored one.</summary>
    public DbSettings ToDbSettings(string? password = null) => new(Server, Port, Database, User, password ?? EffectivePassword);

    /// <summary>The saved settings, or the defaults when there is no (readable) file.</summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A missing or corrupt file just means starting from the defaults.
        }
        return new AppSettings();
    }

    /// <summary>Writes the settings file; failures are ignored.</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not remembering the settings is not worth interrupting the user over.
        }
    }

    /// <summary>Encrypts for the current Windows user (DPAPI) and returns base64; null for no password.</summary>
    private static string? Protect(string? plain) =>
        string.IsNullOrEmpty(plain)
            ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    private static string? Unprotect(string? protectedText)
    {
        if (string.IsNullOrEmpty(protectedText))
            return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedText), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Another user or machine (e.g. a copied settings file): the password must be entered again.
            return null;
        }
    }
}
