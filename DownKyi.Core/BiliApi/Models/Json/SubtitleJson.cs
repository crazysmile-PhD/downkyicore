using System.Text;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Models.Json;

public class SubtitleJson : BaseModel
{
    [JsonProperty("font_size")] public float FontSize { get; set; }
    [JsonProperty("font_color")] public string FontColor { get; set; } = string.Empty;
    [JsonProperty("background_alpha")] public float BackgroundAlpha { get; set; }
    [JsonProperty("background_color")] public string BackgroundColor { get; set; } = string.Empty;
    [JsonProperty("Stroke")] public string Stroke { get; set; } = string.Empty;
    [JsonProperty("body")] public List<Subtitle> Body { get; set; } = new();

    /// <summary>
    /// srt格式字幕
    /// </summary>
    /// <returns></returns>
    public string ToSubRip()
    {
        var subRip = new StringBuilder();
        for (int i = 0; i < Body.Count; i++)
        {
            subRip.AppendLine((i + 1).ToString());
            subRip.AppendLine($"{SecondsToSrtTime(Body[i].From)} --> {SecondsToSrtTime(Body[i].To)}");
            subRip.AppendLine(Body[i].Content);
            subRip.AppendLine();
        }

        return subRip.ToString();
    }

    /// <summary>
    /// 秒数转 时:分:秒 格式
    /// </summary>
    /// <param name="seconds"></param>
    /// <returns></returns>
    private static string SecondsToSrtTime(decimal seconds)
    {
        if (seconds < 0) return "00:00:00,000";

        var totalMilliseconds = decimal.ToInt64(decimal.Round(seconds * 1000m, 0, MidpointRounding.AwayFromZero));
        var span = TimeSpan.FromMilliseconds(totalMilliseconds);
        return $"{(int)span.TotalHours:D2}:{span.Minutes:D2}:{span.Seconds:D2},{span.Milliseconds:D3}";
    }
}
