namespace MediaPlayer.AndroidTv;

/// <summary>Pure sizing rules shared by the MAUI layout and its platform independent tests.</summary>
public static class ResponsiveLayoutMetrics
{
    public const double CompactBreakpoint = 600;

    public static double GetViewportWidth(double rootWidth, double pageWidth, double contentWidth) =>
        rootWidth > 0 ? rootWidth : pageWidth > 0 ? pageWidth : contentWidth;

    public static bool IsCompact(double width, bool isWindowsDesktop = false, bool isPhone = false) =>
        !isWindowsDesktop && (isPhone || width > 0 && width <= CompactBreakpoint);

    public static double GetPosterWidth(double viewportWidth, bool isCompact, bool isWindowsDesktop)
    {
        if (isCompact) return Math.Clamp(viewportWidth * 0.34, 112, 150);
        if (!isWindowsDesktop) return 100;
        return Math.Clamp(viewportWidth * 0.12, 145, 210);
    }

    public static double GetHeroHeight(double viewportHeight, double headerHeight, bool isCompact) => isCompact
        ? Math.Clamp(viewportHeight * 0.68, 360, 500)
        : Math.Clamp((viewportHeight + headerHeight) * 0.78, 625, 790);
}
