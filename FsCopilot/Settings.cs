namespace FsCopilot;

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// The app's persisted preferences: <c>settings.json</c> beside the executable, next to the
/// downloaded profiles in <c>Definitions/</c>. Loaded once at start; every change is saved
/// shortly after it happens. A file that cannot be read yields defaults, never a failure.
/// Serialisation is source-generated so trimming cannot remove what it needs.
/// </summary>
public sealed class Settings
{
    public static readonly string Path = System.IO.Path.Combine(AppContext.BaseDirectory, "settings.json");
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly Lock _lock = new();
    private Timer? _pending;

    /// <summary>Exe name of the ATC app the user picked, or null for whichever known app is running.</summary>
    public string? AtcApp { get; set; }
    public double AtcVolume { get; set; } = 1.0;
    public bool AtcMuted { get; set; }

    public static Settings Load()
    {
        try
        {
            if (!File.Exists(Path)) return new Settings();
            var json = File.ReadAllText(Path);
            var settings = JsonSerializer.Deserialize(json, SettingsContext.Default.Settings);
            if (settings is null) return new Settings();
            settings.AtcVolume = Math.Clamp(settings.AtcVolume, 0, 1);
            Log.Information("[Settings] Loaded {Path}", Path);
            return settings;
        }
        catch (Exception e)
        {
            Log.Warning(e, "[Settings] Could not read {Path}; using defaults", Path);
            return new Settings();
        }
    }

    /// <summary>Write soon. Several changes in quick succession become one write.</summary>
    public void Save()
    {
        lock (_lock)
        {
            _pending ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            _pending.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Write now. For exit, where a pending debounce would be lost.</summary>
    public void Flush()
    {
        lock (_lock)
        {
            _pending?.Change(Timeout.Infinite, Timeout.Infinite);
            try
            {
                var json = JsonSerializer.Serialize(this, SettingsContext.Default.Settings);
                var temp = Path + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, Path, overwrite: true);
            }
            catch (Exception e)
            {
                Log.Warning(e, "[Settings] Could not write {Path}", Path);
            }
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Settings))]
internal partial class SettingsContext : JsonSerializerContext;
