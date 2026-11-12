using MediaPlayer.AndroidTv;

public sealed class MediaImageUriTests
{
    [Fact]
    public void ResolvesServerRootRelativeArtworkPath()
    {
        var uri = MediaImageUri.Resolve("/api/catalog/image/w500/poster.jpg", "http://192.168.1.20:6050");

        Assert.Equal("http://192.168.1.20:6050/api/catalog/image/w500/poster.jpg", uri?.AbsoluteUri);
    }

    [Fact]
    public void ServerRelativeImagePathUsesConfiguredServer()
    {
        var uri = MediaImageUri.Resolve("/api/catalog/image/w500/poster.jpg", "http://192.168.86.100:6050");

        Assert.Equal("http://192.168.86.100:6050/api/catalog/image/w500/poster.jpg", uri?.AbsoluteUri);
    }

    [Fact]
    public void AbsoluteImageUriRemainsAbsolute()
    {
        var uri = MediaImageUri.Resolve("https://images.example/poster.jpg", "http://192.168.86.100:6050");

        Assert.Equal("https://images.example/poster.jpg", uri?.AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void EmptyImagePathReturnsNull(string? imagePath)
    {
        Assert.Null(MediaImageUri.Resolve(imagePath, "http://192.168.86.100:6050"));
    }
}
