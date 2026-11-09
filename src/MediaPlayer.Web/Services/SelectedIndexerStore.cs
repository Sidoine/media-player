using System.Text.Json;

namespace media_player.Services;

public sealed class SelectedIndexerStore(IWebHostEnvironment environment)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string file = Path.Combine(environment.ContentRootPath, "App_Data", "selected-indexers.json");

    public async Task<List<SelectedIndexer>> GetAllAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return await ReadUnlockedAsync(ct); }
        finally { gate.Release(); }
    }

    public async Task<SelectedIndexer> AddAsync(SelectedIndexer source, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var items = await ReadUnlockedAsync(ct);
            var existing = items.FirstOrDefault(x => string.Equals(x.DefinitionId, source.DefinitionId, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return existing;
            items.Add(source);
            await WriteUnlockedAsync(items, ct);
            return source;
        }
        finally { gate.Release(); }
    }

    public async Task<bool> RemoveAsync(Guid id, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var items = await ReadUnlockedAsync(ct);
            var removed = items.RemoveAll(x => x.Id == id) > 0;
            if (removed) await WriteUnlockedAsync(items, ct);
            return removed;
        }
        finally { gate.Release(); }
    }

    private async Task<List<SelectedIndexer>> ReadUnlockedAsync(CancellationToken ct)
    {
        if (!File.Exists(file)) return [];
        try
        {
            await using var stream = File.OpenRead(file);
            return await JsonSerializer.DeserializeAsync<List<SelectedIndexer>>(stream, cancellationToken: ct) ?? [];
        }
        catch (JsonException) { return []; }
    }

    private async Task WriteUnlockedAsync(List<SelectedIndexer> items, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await using var stream = File.Create(file);
        await JsonSerializer.SerializeAsync(stream, items, cancellationToken: ct);
    }
}

public sealed record SelectedIndexer(Guid Id, string DefinitionId, string Name, bool RightsConfirmed, DateTimeOffset AddedAt);
public sealed record SelectedIndexerInput(string DefinitionId, bool RightsConfirmed);
