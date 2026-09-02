using System.Text.Json;

namespace DistantWorlds2.AssetExtractor;

// Remembers the last-used install/output folders, and the last bundle selection, so the user isn't
// forced to re-pick them on every run. Deliberately does NOT remember asset-type selections — those
// are expected to vary run to run more than bundle picks do.
internal sealed class UserSettings
{
    private const int DefaultFfmpegTimeoutSeconds = 300;
    private static int DefaultMaxParallelBundles
    {
        get
        {
            var logicalProcessors = Environment.ProcessorCount;
            if (logicalProcessors <= 8)
                return Math.Max(1, logicalProcessors);
            return Math.Max(1, logicalProcessors / 2);
        }
    }

    // Unlike DefaultMaxParallelBundles (halved above 8 cores because each bundle worker used to also do
    // its own inline ffmpeg-spawning conversion work), this pool is now the sole knob governing
    // CPU/ffmpeg-bound conversion concurrency, so a plain processor count is the right default.
    private static int DefaultMaxParallelConversions => Math.Max(1, Environment.ProcessorCount);

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "dw2extract", "settings.json");

    public string? InstallDir { get; set; }
    public string? OutputDir { get; set; }

    // Null means "all bundles" — either nothing has been picked yet, or the user's last pick covered
    // every bundle. Only an actual partial pick gets stored as an explicit list, so a full DW2 update
    // that adds new bundles doesn't leave them silently unchecked next run.
    public List<string>? LastSelectedBundles { get; set; }

    // Same null-means-"all" convention as LastSelectedBundles. Stored as Program.AssetKindFlags flag
    // names (e.g. "Dds", "Png") rather than the enum itself, so this settings type stays decoupled from
    // Program's internal enum.
    public List<string>? LastSelectedAssetTypes { get; set; }

    // Generic default timeout for external ffmpeg process calls.
    public int? FfmpegTimeoutSeconds { get; set; }

    // Specific overrides; if unset/invalid they fall back to FfmpegTimeoutSeconds.
    public int? TextureFfmpegTimeoutSeconds { get; set; }
    public int? SoundFfmpegTimeoutSeconds { get; set; }
    public int? MaxParallelBundles { get; set; }
    public int? MaxParallelConversions { get; set; }

    public int GetTextureFfmpegTimeoutSeconds() => ResolveTimeout(TextureFfmpegTimeoutSeconds, FfmpegTimeoutSeconds);
    public int GetSoundFfmpegTimeoutSeconds() => ResolveTimeout(SoundFfmpegTimeoutSeconds, FfmpegTimeoutSeconds);
    public int GetMaxParallelBundles() => MaxParallelBundles is > 0 ? MaxParallelBundles.Value : DefaultMaxParallelBundles;
    public int GetMaxParallelConversions() => MaxParallelConversions is > 0 ? MaxParallelConversions.Value : DefaultMaxParallelConversions;

    private static int ResolveTimeout(int? specific, int? generic)
    {
        if (specific is > 0)
            return specific.Value;
        if (generic is > 0)
            return generic.Value;
        return DefaultFfmpegTimeoutSeconds;
    }

    private bool EnsureTimeoutDefaults()
    {
        var changed = false;

        if (FfmpegTimeoutSeconds is null or <= 0)
        {
            FfmpegTimeoutSeconds = DefaultFfmpegTimeoutSeconds;
            changed = true;
        }

        if (TextureFfmpegTimeoutSeconds is null or <= 0)
        {
            TextureFfmpegTimeoutSeconds = FfmpegTimeoutSeconds;
            changed = true;
        }

        if (SoundFfmpegTimeoutSeconds is null or <= 0)
        {
            SoundFfmpegTimeoutSeconds = FfmpegTimeoutSeconds;
            changed = true;
        }

        if (MaxParallelBundles is null or <= 0)
        {
            MaxParallelBundles = DefaultMaxParallelBundles;
            changed = true;
        }

        if (MaxParallelConversions is null or <= 0)
        {
            MaxParallelConversions = DefaultMaxParallelConversions;
            changed = true;
        }

        return changed;
    }

    public static UserSettings Load()
    {
        UserSettings settings;
        var fileExists = File.Exists(SettingsPath);

        try
        {
            settings = fileExists
                ? (JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(SettingsPath)) ?? new UserSettings())
                : new UserSettings();
        }
        catch
        {
            // Missing, corrupt, or unreadable — just start fresh rather than fail the whole run over it.
            settings = new UserSettings();
            fileExists = false;
        }

        var changed = settings.EnsureTimeoutDefaults();
        if (!fileExists || changed)
            settings.Save();

        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this));
        }
        catch
        {
            // Non-fatal — worst case the user gets asked to browse again next run.
        }
    }

    public static void Reset()
    {
        try
        {
            if (File.Exists(SettingsPath))
                File.Delete(SettingsPath);
        }
        catch
        {
            // Non-fatal — if deletion fails, Load() will just return fresh settings anyway.
        }
    }
}
