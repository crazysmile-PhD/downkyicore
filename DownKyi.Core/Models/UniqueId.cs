using System;
using System.Xml.Serialization;

namespace DownKyi.Models;

[Serializable]
public class UniqueId
{
    public UniqueId()
    {
    }

    public UniqueId(string type, string value)
    {
        Type = type;
        Value = value;
    }

    [XmlAttribute("type")]
    public string Type { get; set; } = string.Empty;

    [XmlText]
    public string Value { get; set; } = string.Empty;
}
