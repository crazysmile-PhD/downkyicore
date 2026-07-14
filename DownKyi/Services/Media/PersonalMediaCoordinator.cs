using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Core.BiliApi.History;
using DownKyi.Core.BiliApi.History.Models;
using DownKyi.Core.Settings;
using DownKyi.Core.Utils;
using DownKyi.Images;
using DownKyi.Utils;
using DownKyi.ViewModels.PageViewModels;
using Prism.Events;

namespace DownKyi.Services.Media;

internal sealed record HistoryPageSnapshot(
    IReadOnlyList<HistoryMedia> Medias,
    long NextMax,
    long NextViewAt,
    bool HasMore);

internal interface IPersonalMediaCoordinator
{
    Task<IReadOnlyList<ToViewMedia>> LoadToViewAsync(
        IEventAggregator eventAggregator,
        CancellationToken cancellationToken);

    Task<HistoryPageSnapshot> LoadHistoryPageAsync(
        long max,
        long viewAt,
        int pageSize,
        IEventAggregator eventAggregator,
        CancellationToken cancellationToken);
}

internal sealed class PersonalMediaCoordinator : IPersonalMediaCoordinator
{
    private readonly ISettingsStore _settingsStore;

    public PersonalMediaCoordinator(ISettingsStore settingsStore)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
    }

    public Task<IReadOnlyList<ToViewMedia>> LoadToViewAsync(
        IEventAggregator eventAggregator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eventAggregator);
        return Task.Run<IReadOnlyList<ToViewMedia>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var items = ToView.GetToView(cancellationToken);
            if (items == null || items.Count == 0)
            {
                return Array.Empty<ToViewMedia>();
            }

            var result = new List<ToViewMedia>(items.Count);
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(new ToViewMedia(eventAggregator, _settingsStore)
                {
                    Aid = item.Aid,
                    Bvid = item.Bvid,
                    UpMid = item.Owner?.Mid ?? -1,
                    Cover = NormalizeImageAddress(item.Pic),
                    Title = item.Title,
                    UpName = item.Owner?.Name ?? string.Empty,
                    UpHeader = item.Owner?.Face ?? string.Empty
                });
            }

            return result;
        }, cancellationToken);
    }

    public Task<HistoryPageSnapshot> LoadHistoryPageAsync(
        long max,
        long viewAt,
        int pageSize,
        IEventAggregator eventAggregator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eventAggregator);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = HistoryApi.GetHistory(
                max,
                viewAt,
                pageSize,
                cancellationToken: cancellationToken);
            var medias = response?.List?
                .Select(item => ConvertHistory(item, eventAggregator, _settingsStore))
                .Where(item => item != null && !string.IsNullOrEmpty(item.Title))
                .Cast<HistoryMedia>()
                .ToArray() ?? Array.Empty<HistoryMedia>();
            return new HistoryPageSnapshot(
                medias,
                response?.Cursor?.Max ?? max,
                response?.Cursor?.ViewAt ?? viewAt,
                medias.Length > 0);
        }, cancellationToken);
    }

    internal static HistoryMedia? ConvertHistory(
        HistoryList history,
        IEventAggregator eventAggregator,
        ISettingsStore settingsStore)
    {
        ArgumentNullException.ThrowIfNull(eventAggregator);
        ArgumentNullException.ThrowIfNull(settingsStore);
        if (history?.History == null || history.History.Business is not ("archive" or "pgc"))
        {
            return null;
        }

        var address = history.History.Business == "archive"
            ? $"https://www.bilibili.com/video/{history.History.Bvid}"
            : history.Address;
        return new HistoryMedia(eventAggregator, settingsStore)
        {
            Business = history.History.Business,
            Bvid = history.History.Bvid ?? string.Empty,
            Url = address,
            UpMid = history.AuthorMid,
            Cover = string.IsNullOrEmpty(history.Cover)
                ? "avares://DownKyi/Resources/video-placeholder.png"
                : NormalizeImageAddress(history.Cover),
            Title = history.Title ?? string.Empty,
            SubTitle = history.ShowTitle ?? string.Empty,
            Duration = history.Duration,
            TagName = history.TagName ?? string.Empty,
            Partdesc = history.NewDesc ?? string.Empty,
            Progress = BuildProgressText(history.Progress),
            Platform = GetPlatformIcon(history.History.Dt),
            UpName = history.AuthorFace != null ? history.AuthorName ?? string.Empty : string.Empty,
            UpHeader = history.AuthorFace ?? string.Empty,
            PartdescVisibility = !string.IsNullOrEmpty(history.NewDesc),
            UpAndTagVisibility = history.History.Business == "archive"
        };
    }

    private static string NormalizeImageAddress(string address)
    {
        return address.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? address
            : $"https:{address}";
    }

    private static VectorImage? GetPlatformIcon(int deviceType)
    {
        return deviceType switch
        {
            1 or 3 or 5 or 7 => NormalIcon.Instance().PlatformMobile,
            2 => NormalIcon.Instance().PlatformPC,
            4 or 6 => NormalIcon.Instance().PlatformIpad,
            33 => NormalIcon.Instance().PlatformTV,
            _ => null
        };
    }

    private static string BuildProgressText(long progress)
    {
        return progress switch
        {
            -1 => DictionaryResource.GetString("HistoryFinished"),
            0 => DictionaryResource.GetString("HistoryStarted"),
            _ => $"{DictionaryResource.GetString("HistoryWatch")} {Format.FormatDuration3(progress)}"
        };
    }
}
