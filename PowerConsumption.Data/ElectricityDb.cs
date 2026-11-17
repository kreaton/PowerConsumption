using System.Data.Common;
using MySqlConnector;

namespace PowerConsumption.Data;

/// <summary>Where electricity_db is and how to log in. The defaults match MultiApiFetcher's appsettings.json.</summary>
/// <param name="Server">Host name or IP of the MariaDB server.</param>
/// <param name="Port">TCP port.</param>
/// <param name="Database">Database name.</param>
/// <param name="User">Login.</param>
/// <param name="Password">Password, or null to try without one.</param>
public sealed record DbSettings(
    string Server = "127.0.0.1",
    int Port = 3306,
    string Database = "electricity_db",
    string User = "perh",
    string? Password = null)
{
    /// <summary>
    /// The connection string. DATETIME columns hold UTC (MultiApiFetcher writes them so), and
    /// are read back as <see cref="DateTimeKind.Utc"/>.
    /// </summary>
    public string ConnectionString => new MySqlConnectionStringBuilder
    {
        Server = Server,
        Port = (uint)Port,
        Database = Database,
        UserID = User,
        Password = Password ?? "",
        DateTimeKind = MySqlDateTimeKind.Utc,
        // Fail fast when the server is down, so the app can show the settings instead of hanging.
        ConnectionTimeout = 5,
        // The all-years report pivots ~175,000 price rows; allow it time on a slow machine.
        DefaultCommandTimeout = 60
    }.ConnectionString;
}

/// <summary>Opens connections to electricity_db.</summary>
public sealed class ElectricityDb(DbSettings settings)
{
    /// <summary>The connection settings this instance was made with.</summary>
    public DbSettings Settings { get; } = settings;

    /// <summary>
    /// An open connection; the caller disposes it. Connections are pooled by MySqlConnector,
    /// so opening one per query is cheap.
    /// </summary>
    public async Task<DbConnection> OpenAsync(CancellationToken ct = default)
    {
        // Dispose the connection if opening fails, so a bad password does not leak it.
        var connection = new MySqlConnection(Settings.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
