using System.Text.Json;
using System.Globalization;

namespace media_player.Services;

public sealed class ExternalStreamingSettingsStore(IWebHostEnvironment environment)
{
    public static IReadOnlyList<StreamingCountry> Countries { get; } = CultureInfo.GetCultures(CultureTypes.SpecificCultures)
        .Select(culture => { try { var region = new RegionInfo(culture.Name); return new StreamingCountry(region.TwoLetterISORegionName, region.EnglishName); } catch (ArgumentException) { return null; } })
        .Where(country => country is not null).Cast<StreamingCountry>().DistinctBy(country => country.Code)
        .OrderBy(country => country.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();

    public static bool IsSupportedCountry(string code) => Countries.Any(country => string.Equals(country.Code, code, StringComparison.OrdinalIgnoreCase));
    private readonly string filePath = Path.Combine(environment.ContentRootPath, "App_Data", "external-streaming.json");
    private readonly SemaphoreSlim gate = new(1, 1);
    private ExternalStreamingSettings? cached;

    public async Task<ExternalStreamingSettings> GetAsync(CancellationToken ct = default)
    {
        if (cached is not null) return cached;
        await gate.WaitAsync(ct);
        try
        {
            if (cached is not null) return cached;
            if (!File.Exists(filePath)) return cached = ExternalStreamingSettings.Default;
            try
            {
                await using var stream = File.OpenRead(filePath);
                return cached = await JsonSerializer.DeserializeAsync<ExternalStreamingSettings>(stream, cancellationToken: ct) ?? ExternalStreamingSettings.Default;
            }
            catch (JsonException) { return cached = ExternalStreamingSettings.Default; }
        }
        finally { gate.Release(); }
    }

    public async Task<ExternalStreamingSettings> SaveAsync(ExternalStreamingSettings settings, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            await using var stream = File.Create(filePath);
            await JsonSerializer.SerializeAsync(stream, settings, cancellationToken: ct);
            return cached = settings;
        }
        finally { gate.Release(); }
    }
}

public sealed record ExternalStreamingSettings(string CountryCode, bool NetflixEnabled, bool PrimeVideoEnabled)
{
    public static ExternalStreamingSettings Default { get; } = new("FR", true, true);
}

public sealed record StreamingProviderAvailability(string Name, string LogoUrl, string SearchUrl);
public sealed record StreamingCountry(string Code, string Name);
