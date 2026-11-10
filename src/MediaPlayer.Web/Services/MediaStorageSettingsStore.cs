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
                    return saved with { CacheRoot = Path.GetFullPath(saved.CacheRoot) };
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or NotSupportedException) { }

        var configuredRoot = configuration["Media:CacheRoot"];
        var root = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(environment.ContentRootPath, "App_Data", "media-cache")
            : Path.GetFullPath(Path.IsPathRooted(configuredRoot) ? configuredRoot : Path.Combine(environment.ContentRootPath, configuredRoot));
        var maxBytes = configuration.GetValue<long?>("Media:CacheMaxBytes") ?? 500L * 1024 * 1024 * 1024;
        var ratio = configuration.GetValue<double?>("Torrents:ShareRatioLimit") ?? 1.0;
        return new MediaStorageSettings(root, Math.Max(100L * 1024 * 1024, maxBytes), Math.Max(0, ratio));
    }
}

public sealed record MediaStorageSettings(string CacheRoot, long CacheMaxBytes, double? ShareRatioLimit);
