using System.Text.Json;

namespace media_player.Services;

public sealed class BittorrentTrackerStore(IWebHostEnvironment environment, IConfiguration configuration)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string file = Path.Combine(environment.ContentRootPath, "App_Data", "bittorrent-trackers.json");

    public async Task<List<string>> GetAllAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (File.Exists(file))
            {
                await using var stream = File.OpenRead(file);
                return await JsonSerializer.DeserializeAsync<List<string>>(stream, cancellationToken: ct) ?? [];
            }
            return configuration.GetSection("Torrents:Trackers").Get<string[]>()?.ToList() ?? [];
        }
        catch (JsonException) { return []; }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(IReadOnlyList<string> urls, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temporary = file + ".tmp";
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, urls, cancellationToken: ct);
            File.Move(temporary, file, overwrite: true);
        }
        finally { gate.Release(); }
    }
}
