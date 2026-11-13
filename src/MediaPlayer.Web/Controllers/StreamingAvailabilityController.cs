using System.Text.Json;
using media_player.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace media_player.Controllers;

[ApiController]
[Route("api/streaming")]
public sealed class StreamingAvailabilityController(ExternalStreamingSettingsStore settings, IHttpClientFactory clients) : ControllerBase
{
    [HttpGet("settings")]
    public Task<ExternalStreamingSettings> GetSettings(CancellationToken ct) => settings.GetAsync(ct);

    [HttpPut("settings"), Authorize(Policy = "AdminWhenMultiUserEnabled")]
    public async Task<IActionResult> SaveSettings([FromBody] ExternalStreamingSettings input, CancellationToken ct)
    {
        var country = input.CountryCode?.Trim().ToUpperInvariant();
        if (country is null || !ExternalStreamingSettingsStore.IsSupportedCountry(country)) return BadRequest(new { error = "Choisissez un pays de référence valide." });
        var saved = await settings.SaveAsync(input with { CountryCode = country }, ct);
        return Ok(saved);
    }

    [HttpGet("{mediaType}/{id:int}")]
    public async Task<IActionResult> GetAvailability(string mediaType, int id, [FromQuery] string? title, CancellationToken ct)
    {
        if (mediaType is not ("movie" or "tv") || id <= 0) return BadRequest();
        var preference = await settings.GetAsync(ct);
        if (!preference.NetflixEnabled && !preference.PrimeVideoEnabled) return Ok(Array.Empty<StreamingProviderAvailability>());
        try
        {
            using var response = await clients.CreateClient("tmdb").GetAsync($"{mediaType}/{id}/watch/providers", ct);
            if (!response.IsSuccessStatusCode) return Ok(Array.Empty<StreamingProviderAvailability>());
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            if (!json.RootElement.TryGetProperty("results", out var results)
                || !results.TryGetProperty(preference.CountryCode, out var country)
                || !country.TryGetProperty("flatrate", out var providers)
                || providers.ValueKind != JsonValueKind.Array)
                return Ok(Array.Empty<StreamingProviderAvailability>());

            var encodedTitle = Uri.EscapeDataString(title ?? "");
            var matches = providers.EnumerateArray().Select(provider =>
            {
                var name = provider.TryGetProperty("provider_name", out var nameValue) ? nameValue.GetString() : null;
                var logo = provider.TryGetProperty("logo_path", out var logoValue) ? logoValue.GetString() : null;
                var isNetflix = preference.NetflixEnabled && string.Equals(name, "Netflix", StringComparison.OrdinalIgnoreCase);
                var isPrime = preference.PrimeVideoEnabled && (name?.Contains("Prime Video", StringComparison.OrdinalIgnoreCase) == true
                    || string.Equals(name, "Amazon Video", StringComparison.OrdinalIgnoreCase));
                if ((!isNetflix && !isPrime) || string.IsNullOrWhiteSpace(name)) return null;
                var url = isNetflix ? $"https://www.netflix.com/search?q={encodedTitle}" : $"https://www.primevideo.com/search/ref=atv_nb_sr?phrase={encodedTitle}";
                return new StreamingProviderAvailability(name, string.IsNullOrWhiteSpace(logo) ? "" : $"https://image.tmdb.org/t/p/w92{logo}", url);
            }).Where(provider => provider is not null).Cast<StreamingProviderAvailability>().ToArray();
            return Ok(matches);
        }
        catch (HttpRequestException) { return Ok(Array.Empty<StreamingProviderAvailability>()); }
    }

}
