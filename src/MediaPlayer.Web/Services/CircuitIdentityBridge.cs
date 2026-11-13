using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Components.Authorization;

namespace media_player.Services;

public sealed class CircuitIdentityBridge
{
    private readonly byte[] secret = RandomNumberGenerator.GetBytes(32);

    public string CreateTicket(ClaimsPrincipal principal)
    {
        var username = principal.Identity?.Name;
        if (string.IsNullOrWhiteSpace(username)) return "";
        var role = principal.IsInRole("Administrator") ? "Administrator" : "User";
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}\n{role}\n{DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds()}"));
        var signature = Convert.ToBase64String(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(payload)));
        return $"{payload}.{signature}";
    }

    public ClaimsPrincipal? Validate(string? ticket)
    {
        if (string.IsNullOrWhiteSpace(ticket)) return null;
        var parts = ticket.Split('.', 2);
        if (parts.Length != 2) return null;
        byte[] supplied;
        try { supplied = Convert.FromBase64String(parts[1]); }
        catch (FormatException) { return null; }
        var expected = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(parts[0]));
        if (supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(supplied, expected)) return null;
        try
        {
            var values = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0])).Split('\n');
            if (values.Length != 3 || !long.TryParse(values[2], out var expiry) || expiry < DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                || values[1] is not ("Administrator" or "User")) return null;
            return new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.Name, values[0]), new Claim(ClaimTypes.Role, values[1])
            ], "BlazorCircuit"));
        }
        catch (FormatException) { return null; }
    }
}

public sealed class CircuitIdentityForwardingHandler(AuthenticationStateProvider authenticationState, CircuitIdentityBridge bridge) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var state = await authenticationState.GetAuthenticationStateAsync();
        if (state.User.Identity?.IsAuthenticated == true)
        {
            var ticket = bridge.CreateTicket(state.User);
            if (ticket.Length > 0) request.Headers.TryAddWithoutValidation("X-MediaPlayer-Circuit", ticket);
        }
        return await base.SendAsync(request, cancellationToken);
    }
}
