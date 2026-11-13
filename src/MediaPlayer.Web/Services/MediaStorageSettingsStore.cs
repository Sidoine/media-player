using System.Globalization;
using System.Text.Json;

namespace media_player.Services;

public sealed class MediaStorageSettingsStore(IWebHostEnvironment environment, IConfiguration configuration)
{
    private readonly string settingsPath = Path.Combine(environment.ContentRootPath, "App_Data", "media-settings.json");
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object sync = new();
    private MediaStorageSettings? current;

    public MediaStorageSettings Current
    {
        get
        {
            lock (sync) return current ??= Load();
        }
    }

    public async Task<MediaStorageSettings> SaveAsync(MediaStorageSettings settings, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            var temporary = settingsPath + ".tmp";
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, settings, new JsonSerializerOptions { WriteIndented = true }, ct);
            File.Move(temporary, settingsPath, overwrite: true);
            lock (sync) current = settings;
            return settings;
        }
        finally { gate.Release(); }
    }

    private MediaStorageSettings Load()
    {
        try
        {
            if (File.Exists(settingsPath))
            {
                using var stream = File.OpenRead(settingsPath);
                var saved = JsonSerializer.Deserialize<MediaStorageSettings>(stream);
                if (saved is not null && !string.IsNullOrWhiteSpace(saved.CacheRoot) && saved.CacheMaxBytes > 0
                    && (saved.ShareRatioLimit is null || (double.IsFinite(saved.ShareRatioLimit.Value) && saved.ShareRatioLimit.Value >= 0)))
                {
                    var hasAnonymousActionSetting = false;
                    stream.Position = 0;
                    using (var document = JsonDocument.Parse(stream))
                        hasAnonymousActionSetting = document.RootElement.TryGetProperty("AllowAnonymousTorrentActions", out _)
                            || document.RootElement.TryGetProperty("allowAnonymousTorrentActions", out _);
                    return saved with
                    {
                        CacheRoot = Path.GetFullPath(saved.CacheRoot),
                        PreferredResolution = IsSupportedResolution(saved.PreferredResolution) ? saved.PreferredResolution : 1080,
                        SubtitleLanguage = IsSupportedSubtitleLanguage(saved.SubtitleLanguage) ? saved.SubtitleLanguage : "auto",
                        AllowAnonymousTorrentActions = hasAnonymousActionSetting ? saved.AllowAnonymousTorrentActions : true
                    };
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or NotSupportedException) { }

        var configuredRoot = configuration["Media:Root"];
        var root = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(environment.ContentRootPath, "App_Data", "media-cache")
            : Path.GetFullPath(Path.IsPathRooted(configuredRoot) ? configuredRoot : Path.Combine(environment.ContentRootPath, configuredRoot));
        var maxBytes = configuration.GetValue<long?>("Media:CacheMaxBytes") ?? 500L * 1024 * 1024 * 1024;
        var ratio = configuration.GetValue<double?>("Torrents:ShareRatioLimit") ?? 1.0;
        return new MediaStorageSettings(root, Math.Max(100L * 1024 * 1024, maxBytes), Math.Max(0, ratio), 1080, false, "auto", true);
    }

    public static bool IsSupportedResolution(int resolution) => resolution is 720 or 1080 or 2160;
    public static bool IsSupportedSubtitleLanguage(string? language) => language is "auto" or "en" or "fr";
    public string ResolvedSubtitleLanguage => ResolveSubtitleLanguage(Current.SubtitleLanguage);
    public static string ResolveSubtitleLanguage(string? preference)
    {
        if (preference is "en" or "fr") return preference;
        var osLanguage = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
        return osLanguage is "" or "iv" ? "en" : osLanguage;
    }
}

public sealed record MediaStorageSettings(string CacheRoot, long CacheMaxBytes, double? ShareRatioLimit,
    int PreferredResolution = 1080, bool EnableUpnp = false, string SubtitleLanguage = "auto", bool AllowAnonymousTorrentActions = true);
