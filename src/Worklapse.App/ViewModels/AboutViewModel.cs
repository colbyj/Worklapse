using Worklapse.App.Updates;

namespace Worklapse.App.ViewModels;

/// <summary>A component Worklapse ships, as the About page lists it.</summary>
public sealed record ThirdPartyComponent(string Name, string License);

/// <summary>
/// Settings' About page: the version, whether an update is waiting, the license, and what Worklapse ships from others.
/// The full texts are in LICENSE.txt and THIRD-PARTY-NOTICES.txt beside the executable.
/// </summary>
public sealed class AboutViewModel(AppUpdates updates)
{
    public AppUpdates Updates { get; } = updates;

    public string VersionText => $"Version {AppUpdates.CurrentVersion}";

    public static string LicensePath => Path.Combine(AppContext.BaseDirectory, "LICENSE.txt");
    public static string NoticesPath => Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");

    public bool HasLicense => File.Exists(LicensePath);
    public bool HasNotices => File.Exists(NoticesPath);

    /// <summary>The list at the top of THIRD-PARTY-NOTICES.txt, without the versions; keep the two alike.</summary>
    public IReadOnlyList<ThirdPartyComponent> Components { get; } =
    [
        new("libaom", "BSD-2-Clause, AOM Patent License 1.0"),
        new("libjpeg-turbo", "IJG, BSD-3-Clause, zlib"),
        new("libwebp", "BSD-3-Clause, Google patent grant"),
        new(".NET runtime", "MIT"),
        new("Avalonia", "MIT"),
        new("ANGLE", "BSD-3-Clause"),
        new("SkiaSharp and HarfBuzzSharp", "MIT"),
        new("SQLite", "Public domain"),
        new("SQLitePCLRaw", "Apache-2.0"),
        new("Microsoft.Data.Sqlite", "MIT"),
        new("Dapper", "Apache-2.0"),
        new("CommunityToolkit.Mvvm", "MIT"),
        new("FluentIcons and Fluent UI System Icons", "MIT"),
        new("Tmds.DBus.Protocol", "MIT"),
        new("MicroCom.Runtime", "MIT"),
        new("Velopack", "MIT"),
    ];
}
