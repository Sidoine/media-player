using MediaPlayer.AndroidTv;

public sealed class PlayerControlVisibilityTests
{
    [Fact]
    public void AndroidUsesSystemBackInsteadOfPlayerReturnButton()
    {
        Assert.False(PlayerControlVisibility.ShowReturnButton(isAndroid: true));
    }

    [Fact]
    public void OtherPlatformsKeepPlayerReturnButton()
    {
        Assert.True(PlayerControlVisibility.ShowReturnButton(isAndroid: false));
    }
}
