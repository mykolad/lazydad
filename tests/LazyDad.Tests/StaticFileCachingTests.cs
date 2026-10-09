using LazyDad.Api.Networking;

namespace LazyDad.Tests;

public class StaticFileCachingTests
{
    [Theory]
    [InlineData("app.js", "e33d99a", "e33d99a", StaticFileCaching.Immutable)]
    [InlineData("app.js", "0ld0ld0", "e33d99a", StaticFileCaching.Revalidate)]
    // A local build's files change without a new version: never kept.
    [InlineData("app.js", "dev", null, StaticFileCaching.Revalidate)]
    [InlineData("index.html", null, "e33d99a", StaticFileCaching.Revalidate)]
    [InlineData("index.html", "e33d99a", "e33d99a", StaticFileCaching.Revalidate)]
    [InlineData("logo.svg", null, "e33d99a", StaticFileCaching.OneDay)]
    [InlineData("favicon.ico", null, null, StaticFileCaching.OneDay)]
    public void For_KeepsAFileOnlyAsLongAsItsUrlStaysTrue(string fileName, string? requestedVersion, string? buildVersion, string expected)
        => Assert.Equal(expected, StaticFileCaching.For(fileName, requestedVersion, buildVersion));
}
