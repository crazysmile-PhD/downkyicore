using System;
using System.Collections.Generic;
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
    public List<string> Genres { get; set; } = new();

    [XmlElement("tag")]
    public List<string> Tags { get; set; } = new();

    [XmlElement("actor")]
    public List<Actor> Actors { get; set; } = new();

    [XmlElement("uniqueid")]
    public UniqueId BilibiliId { get; set; } = null!;

    [XmlElement("premiered")]
    public string Premiered { get; set; } = string.Empty;

    [XmlElement("rating")]
    public List<Rating> Ratings { get; set; } = new();
}

[Serializable]
public class UniqueId
{
    [XmlAttribute("type")]
    public string Type { get; set; } = string.Empty;

    [XmlText]
    public string Value { get; set; } = string.Empty;

    public UniqueId() { }

    public UniqueId(string type, string value)
    {
        Type = type;
        Value = value;
    }
}



[Serializable]
public class Actor
{
    [XmlElement("name")]
    public string Name { get; set; } = string.Empty;

    [XmlElement("role")]
    public string Role { get; set; } = string.Empty;

    public Actor() { }
    public Actor(string name, string role)
    {
        Name = name;
        Role = role;
    }
}

[Serializable]
public class Rating
{
    [XmlAttribute("name")]
    public string Name { get; set; } = string.Empty;


    [XmlAttribute("max")]
    public int Max { get; set; }

    [XmlAttribute("default")]
    public bool IsDefault { get; set; }

    [XmlText]
    public float Value { get; set; }

    public Rating() { }

    public Rating(string name, float value, int max = 10, bool isDefault = false)
    {
        Name = name;
        Value = value;
        Max = max;
        IsDefault = isDefault;
    }
}
