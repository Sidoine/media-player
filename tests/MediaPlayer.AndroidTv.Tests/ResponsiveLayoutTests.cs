using MediaPlayer.AndroidTv;

public sealed class ResponsiveLayoutTests
{
    [Fact]
    public void ViewportWidthUsesRootBeforeNotYetMeasuredScrollContent()
    {
        Assert.Equal(393, ResponsiveLayoutMetrics.GetViewportWidth(393, 393, 0));
        Assert.Equal(393, ResponsiveLayoutMetrics.GetViewportWidth(0, 393, 0));
    }

    [Theory]
    [InlineData(360, true)]
    [InlineData(600, true)]
    [InlineData(601, false)]
    [InlineData(1920, false)]
    public void CompactModeUsesAvailableWidth(double width, bool expected)
    {
        Assert.Equal(expected, ResponsiveLayoutMetrics.IsCompact(width));
    }

    [Fact]
    public void LandscapePhoneKeepsCompactControls()
    {
        Assert.True(ResponsiveLayoutMetrics.IsCompact(844, isPhone: true));
        Assert.False(ResponsiveLayoutMetrics.IsCompact(844));
        Assert.True(ResponsiveLayoutMetrics.IsCompact(0, isPhone: true));
    }

    [Fact]
    public void WindowsDesktopDoesNotUseCompactModeEvenInNarrowWindow()
    {
        Assert.False(ResponsiveLayoutMetrics.IsCompact(390, isWindowsDesktop: true));
    }

    [Theory]
    [InlineData(360, 122.4)]
    [InlineData(600, 150)]
    public void PhonePosterWidthFitsViewport(double viewportWidth, double expected)
    {
        Assert.Equal(expected, ResponsiveLayoutMetrics.GetPosterWidth(viewportWidth, isCompact: true, isWindowsDesktop: false), 1);
    }

    [Fact]
    public void PhoneHeroHeightStaysWithinUsableBounds()
    {
        Assert.Equal(360, ResponsiveLayoutMetrics.GetHeroHeight(400, 78, isCompact: true));
        Assert.Equal(500, ResponsiveLayoutMetrics.GetHeroHeight(900, 78, isCompact: true));
    }

    [Fact]
    public void TvHeroKeepsLargeScreenSizing()
    {
        Assert.Equal(790, ResponsiveLayoutMetrics.GetHeroHeight(1200, 78, isCompact: false));
        Assert.Equal(210, ResponsiveLayoutMetrics.GetPosterWidth(1920, isCompact: false, isWindowsDesktop: true));
    }
}
