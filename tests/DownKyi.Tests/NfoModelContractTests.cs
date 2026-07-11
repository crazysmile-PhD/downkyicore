using System.Xml.Serialization;
using DownKyi.Models;

namespace DownKyi.Tests;

public sealed class NfoModelContractTests
{
    [Fact]
    public void MovieMetadataCollectionsRoundTripThroughXmlSerializer()
    {
        var metadata = new MovieMetadata
        {
            Title = "Episode 1",
            BilibiliId = new UniqueId("bilibili", "BV1example")
        };
        metadata.Genres.Add("Technology");
        metadata.Tags.Add("Space");
        metadata.Actors.Add(new Actor("Uploader", "42"));
        metadata.Ratings.Add(new Rating("bilibili", 9.5f, isDefault: true));

        var serializer = new XmlSerializer(typeof(MovieMetadata));
        using var writer = new StringWriter();
        serializer.Serialize(writer, metadata);
        using var reader = new StringReader(writer.ToString());
        var restored = Assert.IsType<MovieMetadata>(serializer.Deserialize(reader));

        Assert.Equal("Technology", Assert.Single(restored.Genres));
        Assert.Equal("Space", Assert.Single(restored.Tags));
        Assert.Equal("Uploader", Assert.Single(restored.Actors).Name);
        Assert.Equal(9.5f, Assert.Single(restored.Ratings).Value);
    }
}
