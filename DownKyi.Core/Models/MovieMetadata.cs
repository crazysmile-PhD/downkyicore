using System;
using System.Collections.ObjectModel;
using System.Xml.Serialization;

namespace DownKyi.Models;

[Serializable]
[XmlRoot("movie")]
public class MovieMetadata
{
    [XmlElement("title")]
    public string Title { get; set; } = string.Empty;

    [XmlElement("plot")]
    public string Plot { get; set; } = string.Empty;

    [XmlElement("year")]
    public string Year { get; set; } = string.Empty;

    [XmlElement("genre")]
    public Collection<string> Genres { get; } = new();

    [XmlElement("tag")]
    public Collection<string> Tags { get; } = new();

    [XmlElement("actor")]
    public Collection<Actor> Actors { get; } = new();

    [XmlElement("uniqueid")]
    public UniqueId BilibiliId { get; set; } = null!;

    [XmlElement("premiered")]
    public string Premiered { get; set; } = string.Empty;

    [XmlElement("rating")]
    public Collection<Rating> Ratings { get; } = new();
}
