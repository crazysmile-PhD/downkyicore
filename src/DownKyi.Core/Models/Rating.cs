using System;
using System.Xml.Serialization;

namespace DownKyi.Models;

[Serializable]
public class Rating
{
    public Rating()
    {
    }

    public Rating(string name, float value, int max = 10, bool isDefault = false)
    {
        Name = name;
        Value = value;
        Max = max;
        IsDefault = isDefault;
    }

    [XmlAttribute("name")]
    public string Name { get; set; } = string.Empty;

    [XmlAttribute("max")]
    public int Max { get; set; }

    [XmlAttribute("default")]
    public bool IsDefault { get; set; }

    [XmlText]
    public float Value { get; set; }
}
