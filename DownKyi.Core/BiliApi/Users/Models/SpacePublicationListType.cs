using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Users.Models;

public class SpacePublicationListType : BaseModel
{
    [JsonProperty("1")] public SpacePublicationListTypeVideoZone Douga { get; set; } = new();
    [JsonProperty("13")] public SpacePublicationListTypeVideoZone Anime { get; set; } = new();
    [JsonProperty("167")] public SpacePublicationListTypeVideoZone Guochuang { get; set; } = new();
    [JsonProperty("3")] public SpacePublicationListTypeVideoZone Music { get; set; } = new();
    [JsonProperty("129")] public SpacePublicationListTypeVideoZone Dance { get; set; } = new();
    [JsonProperty("4")] public SpacePublicationListTypeVideoZone Game { get; set; } = new();
    [JsonProperty("36")] public SpacePublicationListTypeVideoZone Technology { get; set; } = new();
    [JsonProperty("188")] public SpacePublicationListTypeVideoZone Digital { get; set; } = new();
    [JsonProperty("234")] public SpacePublicationListTypeVideoZone Sports { get; set; } = new();
    [JsonProperty("223")] public SpacePublicationListTypeVideoZone Car { get; set; } = new();
    [JsonProperty("160")] public SpacePublicationListTypeVideoZone Life { get; set; } = new();
    [JsonProperty("211")] public SpacePublicationListTypeVideoZone Food { get; set; } = new();
    [JsonProperty("217")] public SpacePublicationListTypeVideoZone Animal { get; set; } = new();
    [JsonProperty("119")] public SpacePublicationListTypeVideoZone Kichiku { get; set; } = new();
    [JsonProperty("155")] public SpacePublicationListTypeVideoZone Fashion { get; set; } = new();
    [JsonProperty("202")] public SpacePublicationListTypeVideoZone Information { get; set; } = new();
    [JsonProperty("5")] public SpacePublicationListTypeVideoZone Ent { get; set; } = new();
    [JsonProperty("181")] public SpacePublicationListTypeVideoZone Cinephile { get; set; } = new();
    [JsonProperty("177")] public SpacePublicationListTypeVideoZone Documentary { get; set; } = new();
    [JsonProperty("23")] public SpacePublicationListTypeVideoZone Movie { get; set; } = new();
    [JsonProperty("11")] public SpacePublicationListTypeVideoZone Tv { get; set; } = new();
}
