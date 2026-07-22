using DownKyi.Core.BiliApi.Users.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.Tests;

public sealed class PublicationSearchContractTests
{
    [Fact]
    public void FixturePreservesSearchPageCountAndMedia()
    {
        var json = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "BiliApi",
            "JsonSamples",
            "space-publication-search.json"));
        var origin = JsonConvert.DeserializeObject<SpacePublicationOrigin>(json);

        Assert.NotNull(origin?.Data);
        Assert.Equal(35, origin.Data.Page.Count);
        Assert.Equal(2, origin.Data.Page.Pn);
        Assert.Equal("BV1fixture01", Assert.Single(origin.Data.List.Vlist!).Bvid);
    }
}
