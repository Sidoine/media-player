namespace MediaPlayer.AndroidTv;

/// <summary>Turns server-relative artwork paths into absolute URIs usable by native image controls.</summary>
public static class MediaImageUri
{
    public static Uri? Resolve(string? imagePath, string serverUrl)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return null;
        if (!imagePath.StartsWith("/", StringComparison.Ordinal)
            && Uri.TryCreate(imagePath, UriKind.Absolute, out var absolute))
            return absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps ? absolute : null;
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var server)
            || (server.Scheme != Uri.UriSchemeHttp && server.Scheme != Uri.UriSchemeHttps)) return null;

        return new Uri(new Uri(server.GetLeftPart(UriPartial.Authority) + "/"), imagePath.TrimStart('/'));
    }
}
