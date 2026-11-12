using System.Text.RegularExpressions;

namespace media_player.Services;

public sealed partial class TmdbImageCacheService(IHttpClientFactory clients, IWebHostEnvironment environment)
{
    private static readonly HashSet<string> AllowedSizes = ["w500", "w780", "w1280", "original"];
    private readonly string cacheRoot = Path.Combine(environment.ContentRootPath, "App_Data", "catalog-images");
    private readonly Dictionary<string, SemaphoreSlim> locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object locksGate = new();

    public static string? ToLocalUrl(string? tmdbPath, string size)
    {
        if (string.IsNullOrWhiteSpace(tmdbPath) || !AllowedSizes.Contains(size)) return null;
        var fileName = Path.GetFileName(tmdbPath);
        return ImageFileNameRegex().IsMatch(fileName)
            ? $"/api/catalog/image/{size}/{Uri.EscapeDataString(fileName)}"
            : null;
    }

    public static string? ToLocalUrlFromRemote(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Host, "image.tmdb.org", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith("/t/p/", StringComparison.Ordinal)) return url;
        var parts = uri.AbsolutePath["/t/p/".Length..].Split('/', 2);
        return parts.Length == 2 ? ToLocalUrl(parts[1], parts[0]) : url;
    }

    public async Task<(string Path, string ContentType)?> GetCachedImageAsync(string size, string fileName, CancellationToken ct)
    {
        if (!AllowedSizes.Contains(size) || !ImageFileNameRegex().IsMatch(fileName) || Path.GetFileName(fileName) != fileName)
            return null;

        var directory = Path.Combine(cacheRoot, size);
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            SemaphoreSlim imageLock;
            lock (locksGate)
            {
                if (!locks.TryGetValue(path, out var existing)) locks[path] = existing = new SemaphoreSlim(1, 1);
                imageLock = existing;
            }
            await imageLock.WaitAsync(ct);
            try
            {
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(directory);
                    using var response = await clients.CreateClient("tmdb-images").GetAsync($"{size}/{Uri.EscapeDataString(fileName)}", ct);
                    if (!response.IsSuccessStatusCode) return null;
                    var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                    var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    await File.WriteAllBytesAsync(temporaryPath, bytes, ct);
                    File.Move(temporaryPath, path, overwrite: true);
                }
            }
            finally { imageLock.Release(); }
        }

        return (path, ContentTypeFromExtension(Path.GetExtension(fileName)));
    }

    private static string ContentTypeFromExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "image/jpeg"
    };

    [GeneratedRegex("^[A-Za-z0-9_-]+\\.(jpg|jpeg|png|webp)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImageFileNameRegex();
}
