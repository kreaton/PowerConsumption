using System.Globalization;
using System.Reflection;

namespace PowerConsumption.Wpf;

/// <summary>
/// Version, commit and build time, stamped into the assembly by Directory.Build.props and the SDK.
/// Shown in the window title and the About box.
/// </summary>
public static class BuildInfo
{
    private static readonly Assembly Assembly = typeof(BuildInfo).Assembly;

    // "1.0.0+a1b2c3d4…": the SDK appends the git commit after "+" when the build is in a repository.
    private static readonly string[] Informational =
        (Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+', 2);

    /// <summary>Major.Minor.BuildNumber, e.g. "1.0.0".</summary>
    public static string Version { get; } = Informational[0];

    /// <summary>The short git commit the build was made from; null outside a repository.</summary>
    public static string? Commit { get; } =
        Informational.Length > 1 ? Informational[1][..Math.Min(7, Informational[1].Length)] : null;

    /// <summary>When the assembly was built, in UTC; null if the BuildDate metadata is missing.</summary>
    public static DateTime? BuildDate { get; } =
        DateTime.TryParse(
            Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildDate")?.Value,
            CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc)
            ? utc
            : null;

    /// <summary>"v1.0.0 · 2026-11-10 14:32 · a1b2c3d", with the build time in the PC's local time.</summary>
    public static string Display { get; } = string.Join(" · ", new[]
    {
        "v" + Version,
        BuildDate?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        Commit,
    }.Where(part => part is not null));

    /// <summary>The copyright line from Directory.Build.props.</summary>
    public static string Copyright { get; } =
        Assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "";
}
