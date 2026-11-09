using System.Globalization;
using MusicMixer.Infrastructure.YouTube;

namespace MusicMixer.Tests;

public sealed class YoutubeExplodeRegionShimTests
{
    [Fact]
    public void Initialize_RestoresCultureAndIsIdempotent()
    {
        var before = CultureInfo.CurrentCulture;

        YoutubeExplodeRegionShim.Initialize();
        YoutubeExplodeRegionShim.Initialize();

        Assert.Equal(before, CultureInfo.CurrentCulture);
    }
}
