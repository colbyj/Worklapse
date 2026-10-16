using System.Text.Json;
using System.Text.Json.Serialization;

namespace Worklapse.Core.Settings;

/// <summary>
/// Loads and saves <see cref="WorklapseSettings"/> as JSON in the per-user config folder. Settings live
/// apart from the data folder because the data folder itself is one of the settings.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly Lock _gate = new();
    private WorklapseSettings _current;

    /// <exception cref="SettingsFileException">The settings file exists but can't be used.</exception>
    public SettingsStore(string? path = null)
    {
        _path = path ?? DefaultPath;
        _current = Load(_path);
    }

    public static string DefaultPath => Path.Combine(ConfigFolder, "settings.json");

    /// <summary>
    /// The per-user config folder. WORKLAPSE_CONFIG_DIR overrides it, so test runs can use their own
    /// settings (and through them their own data folder) without touching the real ones.
    /// </summary>
    public static string ConfigFolder =>
        Environment.GetEnvironmentVariable("WORKLAPSE_CONFIG_DIR") is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Worklapse");

    public event EventHandler<WorklapseSettings>? Changed;

    public WorklapseSettings Current
    {
        get { lock (_gate) return _current; }
    }

    public void Save(WorklapseSettings settings)
    {
        settings = settings.Normalized();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temp, _path, overwrite: true);
        lock (_gate) _current = settings;
        Changed?.Invoke(this, settings);
    }

    /// <summary>
    /// Renames an unusable settings file out of the way (keeping it for reference), so the next
    /// <see cref="SettingsStore"/> starts from defaults. Returns the new name.
    /// </summary>
    public static string SetAside(string path)
    {
        var aside = $"{path}.broken-{DateTime.Now:yyyyMMdd-HHmmss}";
        File.Move(path, aside);
        return aside;
    }

    private static WorklapseSettings Load(string path)
    {
        if (!File.Exists(path))
            return new WorklapseSettings();

        // A corrupt or unreadable file is surfaced rather than silently replaced with defaults,
        // which would quietly change where data is written.
        try
        {
            var settings = JsonSerializer.Deserialize<WorklapseSettings>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("The file contains null instead of settings.");
            return settings.Normalized();
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new SettingsFileException(path, ex);
        }
    }
}

/// <summary>The settings file exists but can't be used: not valid JSON, a wrong value, or unreadable.</summary>
public sealed class SettingsFileException(string path, Exception inner)
    : Exception($"The settings file '{path}' can't be read: {inner.Message}", inner)
{
    public string Path { get; } = path;
}
