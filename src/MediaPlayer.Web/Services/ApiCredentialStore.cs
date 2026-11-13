using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace media_player.Services;

public sealed class ApiCredentialStore(IConfiguration configuration, IWebHostEnvironment environment, IDataProtectionProvider protection)
{
    private readonly IDataProtector protector = protection.CreateProtector("MediaPlayer.ApiCredentials.v1");
    private readonly string path = Path.Combine(environment.ContentRootPath, "App_Data", "api-credentials.json");

    public string? TmdbToken => configuration["TMDB:ReadAccessToken"] ?? ReadLocal()?.TmdbToken;
    public string? OpenSubtitlesKey => configuration["OpenSubtitles:ApiKey"] ?? ReadLocal()?.OpenSubtitlesKey;
    public bool IsFullyConfigured => !string.IsNullOrWhiteSpace(TmdbToken) && !string.IsNullOrWhiteSpace(OpenSubtitlesKey);

    public object GetStatus() => new
    {
        tmdbConfigured = !string.IsNullOrWhiteSpace(TmdbToken),
        tmdbSource = Source("TMDB:ReadAccessToken", ReadLocal()?.TmdbToken),
        openSubtitlesConfigured = !string.IsNullOrWhiteSpace(OpenSubtitlesKey),
        openSubtitlesSource = Source("OpenSubtitles:ApiKey", ReadLocal()?.OpenSubtitlesKey),
        allConfigured = IsFullyConfigured
    };

    public async Task SaveLocalAsync(string? tmdbToken, string? openSubtitlesKey, CancellationToken ct)
    {
        var current = ReadLocal() ?? new StoredCredentials();
        var updated = new StoredCredentials
        {
            TmdbToken = string.IsNullOrWhiteSpace(tmdbToken) ? current.TmdbToken : tmdbToken.Trim(),
            OpenSubtitlesKey = string.IsNullOrWhiteSpace(openSubtitlesKey) ? current.OpenSubtitlesKey : openSubtitlesKey.Trim()
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encrypted = protector.Protect(JsonSerializer.Serialize(updated));
        await File.WriteAllTextAsync(path + ".tmp", encrypted, ct);
        File.Move(path + ".tmp", path, true);
    }

    private string Source(string configKey, string? local) => !string.IsNullOrWhiteSpace(configuration[configKey]) ? "secret" :
        !string.IsNullOrWhiteSpace(local) ? "local" : "missing";
    private StoredCredentials? ReadLocal()
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<StoredCredentials>(protector.Unprotect(File.ReadAllText(path))) : null; }
        catch (Exception ex) when (ex is IOException or JsonException or CryptographicException) { return null; }
    }
    private sealed class StoredCredentials { public string? TmdbToken { get; init; } public string? OpenSubtitlesKey { get; init; } }
}
