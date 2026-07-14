using System.Globalization;
using System.Text.RegularExpressions;

namespace DownKyi.Core.FileName;

public class FileNameBuilder
{
    private readonly IReadOnlyList<FileNamePart> _nameParts;
    private string _order = "ORDER";
    private string _section = "SECTION";
    private string _mainTitle = "MAIN_TITLE";
    private string _pageTitle = "PAGE_TITLE";
    private string _videoZone = "VIDEO_ZONE";
    private string _audioQuality = "AUDIO_QUALITY";
    private string _videoQuality = "VIDEO_QUALITY";
    private string _videoCodec = "VIDEO_CODEC";

    private string _videoPublishTime = "VIDEO_PUBLISH_TIME";

    private long _avid = -1;
    private string _bvid = "BVID";
    private long _cid = -1;

    private long _upMid = -1;
    private string _upName = "UP_NAME";

    private FileNameBuilder(IReadOnlyList<FileNamePart> nameParts)
    {
        this._nameParts = nameParts;
    }

    public static FileNameBuilder Create(IReadOnlyList<FileNamePart> nameParts)
    {
        return new FileNameBuilder(nameParts);
    }

    public FileNameBuilder SetOrder(int order)
    {
        _order = order.ToString(CultureInfo.InvariantCulture);
        return this;
    }

    public FileNameBuilder SetOrder(int order, int count)
    {
        var length = Math.Abs(count).ToString(CultureInfo.InvariantCulture).Length;
        _order = order.ToString("D" + length, CultureInfo.InvariantCulture);

        return this;
    }

    public FileNameBuilder SetSection(string section)
    {
        _section = section;
        return this;
    }

    public FileNameBuilder SetMainTitle(string mainTitle)
    {
        _mainTitle = mainTitle;
        return this;
    }

    public FileNameBuilder SetPageTitle(string pageTitle)
    {
        _pageTitle = pageTitle;
        return this;
    }

    public FileNameBuilder SetVideoZone(string videoZone)
    {
        _videoZone = videoZone;
        return this;
    }

    public FileNameBuilder SetAudioQuality(string audioQuality)
    {
        _audioQuality = audioQuality;
        return this;
    }

    public FileNameBuilder SetVideoQuality(string videoQuality)
    {
        _videoQuality = videoQuality;
        return this;
    }

    public FileNameBuilder SetVideoCodec(string videoCodec)
    {
        _videoCodec = videoCodec;
        return this;
    }

    public FileNameBuilder SetVideoPublishTime(string videoPublishTime)
    {
        _videoPublishTime = videoPublishTime;
        return this;
    }

    public FileNameBuilder SetAvid(long avid)
    {
        _avid = avid;
        return this;
    }

    public FileNameBuilder SetBvid(string bvid)
    {
        _bvid = bvid;
        return this;
    }

    public FileNameBuilder SetCid(long cid)
    {
        _cid = cid;
        return this;
    }

    public FileNameBuilder SetUpMid(long upMid)
    {
        _upMid = upMid;
        return this;
    }

    public FileNameBuilder SetUpName(string upName)
    {
        _upName = upName;
        return this;
    }

    public string RelativePath()
    {
        var path = string.Empty;

        foreach (var part in _nameParts)
        {
            switch (part)
            {
                case FileNamePart.Order:
                    path += _order;
                    break;
                case FileNamePart.Section:
                    path += _section;
                    break;
                case FileNamePart.MainTitle:
                    path += _mainTitle;
                    break;
                case FileNamePart.PageTitle:
                    path += _pageTitle;
                    break;
                case FileNamePart.VideoZone:
                    path += _videoZone;
                    break;
                case FileNamePart.AudioQuality:
                    path += _audioQuality;
                    break;
                case FileNamePart.VideoQuality:
                    path += _videoQuality;
                    break;
                case FileNamePart.VideoCodec:
                    path += _videoCodec;
                    break;
                case FileNamePart.VideoPublishTime:
                    path += _videoPublishTime;
                    break;
                case FileNamePart.Avid:
                    path += string.Create(CultureInfo.InvariantCulture, $"av{_avid}");
                    break;
                case FileNamePart.Bvid:
                    path += _bvid;
                    break;
                case FileNamePart.Cid:
                    path += _cid.ToString(CultureInfo.InvariantCulture);
                    break;
                case FileNamePart.UpMid:
                    path += _upMid.ToString(CultureInfo.InvariantCulture);
                    break;
                case FileNamePart.UpName:
                    path += _upName;
                    break;
            }

            if ((int)part >= 100)
            {
                path += HyphenSeparated.Hyphen[(int)part];
            }
        }

        // 避免连续多个斜杠
        path = Regex.Replace(path, @"//+", "/");
        // 避免以斜杠开头和结尾的情况
        return path.TrimEnd('/').TrimStart('/');
    }
}
