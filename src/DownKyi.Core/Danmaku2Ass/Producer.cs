namespace DownKyi.Core.Danmaku2Ass;

public class Producer
{
    private readonly CustomDanmakuFilter? _customFilter;
    private readonly CancellationToken _cancellationToken;

    public Dictionary<string, bool> Config { get; }
    public Dictionary<string, Filter> Filters { get; private set; } = new();
    public IReadOnlyList<Danmaku> Danmakus { get; }
    public IReadOnlyList<Danmaku> KeepedDanmakus { get; private set; } = Array.Empty<Danmaku>();
    public Dictionary<string, int> FilterDetail { get; private set; } = new();

    public Producer(
        Dictionary<string, bool> config,
        IReadOnlyList<Danmaku> danmakus,
        CustomDanmakuFilter? customFilter = null,
        CancellationToken cancellationToken = default)
    {
        Config = config;
        Danmakus = danmakus;
        _customFilter = customFilter;
        _cancellationToken = cancellationToken;
    }

    public void StartHandle()
    {
        LoadFilter();
        ApplyFilter();
    }

    public void LoadFilter()
    {
        Filters = new Dictionary<string, Filter>();
        if (Config["top_filter"])
        {
            Filters.Add("top_filter", new TopFilter());
        }

        if (Config["bottom_filter"])
        {
            Filters.Add("bottom_filter", new BottomFilter());
        }

        if (Config["scroll_filter"])
        {
            Filters.Add("scroll_filter", new ScrollFilter());
        }

        if (_customFilter is { IsEnabled: true })
        {
            Filters.Add("custom_filter", _customFilter);
        }
    }

    public void ApplyFilter()
    {
        var filterDetail = new Dictionary<string, int>()
        {
            { "top_filter", 0 },
            { "bottom_filter", 0 },
            { "scroll_filter", 0 }
        };

        if (Filters.ContainsKey("custom_filter"))
        {
            filterDetail.Add("custom_filter", 0);
        }

        var danmakus = Danmakus;
        string[] orders = { "top_filter", "bottom_filter", "scroll_filter", "custom_filter" };
        foreach (var name in orders)
        {
            if (!Filters.TryGetValue(name, out var filter))
            {
                continue;
            }

            _cancellationToken.ThrowIfCancellationRequested();
            var count = danmakus.Count;
            danmakus = filter.DoFilter(danmakus, _cancellationToken);
            filterDetail[name] = count - danmakus.Count;
        }

        KeepedDanmakus = danmakus;
        FilterDetail = filterDetail;
    }

    public Dictionary<string, int> Report()
    {
        var blockedCount = FilterDetail.Values.Sum();

        var passedCount = KeepedDanmakus.Count;
        var totalCount = blockedCount + passedCount;

        var ret = new Dictionary<string, int>
        {
            { "blocked", blockedCount },
            { "passed", passedCount },
            { "total", totalCount }
        };

        foreach (var detail in FilterDetail)
        {
            ret[detail.Key] = detail.Value;
        }

        return ret;
    }
}
