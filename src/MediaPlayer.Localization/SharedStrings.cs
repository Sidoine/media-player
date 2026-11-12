using System.Globalization;
using System.Resources;

namespace MediaPlayer.Localization;

/// <summary>Marker class used by the web app's shared resource localizer.</summary>
public sealed class SharedStrings { }

/// <summary>Resource lookup for native clients that do not use ASP.NET localization services.</summary>
public static class SharedText
{
    private static readonly ResourceManager Resources = new("MediaPlayer.Localization.SharedStrings", typeof(SharedText).Assembly);

    public static string Get(string key) => Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;
}
