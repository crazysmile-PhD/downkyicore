using System;
using System.Xml.Serialization;

namespace DownKyi.Models;

[Serializable]
public class Actor
{
    public Actor()
    {
    }

    public Actor(string name, string role)
    {
        Name = name;
        Role = role;
    }

    [XmlElement("name")]
    public string Name { get; set; } = string.Empty;

    [XmlElement("role")]
    public string Role { get; set; } = string.Empty;
}
