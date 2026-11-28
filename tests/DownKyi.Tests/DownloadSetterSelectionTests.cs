using System.Collections.Specialized;
using DownKyi.Application.Desktop;
using DownKyi.Domain.Downloads;
using DownKyi.Services.Download;
using DownKyi.ViewModels.Dialogs;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class DownloadSetterSelectionTests
{
    [Fact]
    public async Task DownloadCommandReturnsSelectedAndDefaultSubtitleTrackIds()
    {
        using var settings = new TestSettingsStore();
        var interaction = new TestDesktopInteractionContext();
        var viewModel = new DownloadSetterViewModel(
            interaction.Notifications,
            new StubFilePickerService(),
            settings.Store,
            new UnusedSubtitleTrackLoader(),
            NullLogger<DownloadSetterViewModel>.Instance)
        {
            Directory = Path.GetTempPath(),
            DownloadSubtitle = true
        };
        await using var viewModelLifetime = viewModel.ConfigureAwait(true);
        viewModel.OnDialogOpened(DownloadSettingsDialog.CreateRequest(
        [
            new(11, "zh", "中文", 0, "//zh"),
            new(22, "en", "English", 1, "//en")
        ]));
        viewModel.SubtitleTracks[1].IsSelected = true;
        viewModel.SubtitleTracks[0].IsSelected = false;
        viewModel.SubtitleTracks[1].IsDefault = true;
        AppDialogResult? result = null;
        viewModel.CloseRequested += (_, value) => result = value;

        viewModel.DownloadCommand.Execute(null);

        Assert.NotNull(result);
        var selection = DownloadSettingsDialog.DecodeResult(result);
        Assert.True(selection.HasValue);
        Assert.True(selection.Value.RequestedContent.SelectedSubtitleTrackIds.HasValue);
        Assert.Equal([22L], selection.Value.RequestedContent.SelectedSubtitleTrackIds.GetValueOrDefault().ToArray());
        Assert.Equal(22L, selection.Value.RequestedContent.DefaultSubtitleTrackId);
        Assert.Equal("en", viewModel.SubtitleTracks[1].Language);
        Assert.Equal("English", viewModel.SubtitleTracks[1].DisplayLanguage);
        Assert.False(viewModel.SubtitleTracks[0].IsAi);
        Assert.True(viewModel.SubtitleTracks[1].IsAi);
    }

    [Fact]
    public async Task DownloadCommandPreservesSelectionWhenReorderingReentersTheBinding()
    {
        using var settings = new TestSettingsStore();
        var interaction = new TestDesktopInteractionContext();
        var viewModel = new DownloadSetterViewModel(
            interaction.Notifications,
            new StubFilePickerService(),
            settings.Store,
            new UnusedSubtitleTrackLoader(),
            NullLogger<DownloadSetterViewModel>.Instance);
        await using var viewModelLifetime = viewModel.ConfigureAwait(true);
        var selectedDirectory = Path.GetTempPath();
        var reentrantSelection = Path.GetPathRoot(selectedDirectory)!;
        viewModel.DirectoryList.Clear();
        viewModel.DirectoryList.Add("other-directory");
        viewModel.DirectoryList.Add(selectedDirectory);
        viewModel.Directory = selectedDirectory;
        var observedSelections = new List<string>();
        viewModel.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(viewModel.Directory))
            {
                observedSelections.Add(viewModel.Directory);
            }
        };
        viewModel.DirectoryList.CollectionChanged += (_, eventArgs) =>
        {
            if (eventArgs.Action == NotifyCollectionChangedAction.Remove)
            {
                viewModel.Directory = reentrantSelection;
            }
        };

        viewModel.DownloadCommand.Execute(null);

        Assert.Equal(selectedDirectory, viewModel.Directory);
        Assert.Equal(selectedDirectory, viewModel.DirectoryList[0]);
        Assert.Equal(selectedDirectory, settings.Store.Current.Video.SaveVideoRootPath);
        Assert.Equal([reentrantSelection, selectedDirectory], observedSelections);
    }

    [Fact]
    public async Task DownloadAllSelectsOnlyPreferredSubtitleTrackAndDefault()
    {
        using var settings = new TestSettingsStore();
        var interaction = new TestDesktopInteractionContext();
        var viewModel = new DownloadSetterViewModel(
            interaction.Notifications,
            new StubFilePickerService(),
            settings.Store,
            new UnusedSubtitleTrackLoader(),
            NullLogger<DownloadSetterViewModel>.Instance)
        {
            DownloadSubtitle = false
        };
        await using var viewModelLifetime = viewModel.ConfigureAwait(true);
        viewModel.OnDialogOpened(DownloadSettingsDialog.CreateRequest(
        [
            new(11, "zh", "中文", 0, "//zh"),
            new(22, "en", "English", 1, "//en")
        ]));
        viewModel.DownloadAll = true;

        viewModel.DownloadAllCommand.Execute(null);

        Assert.True(viewModel.SubtitleTracks[0].IsSelected);
        Assert.True(viewModel.SubtitleTracks[0].IsDefault);
        Assert.False(viewModel.SubtitleTracks[1].IsSelected);
        Assert.False(viewModel.SubtitleTracks[1].IsDefault);
    }

    [Fact]
    public async Task SelectingSubtitleTrackCanDeriveDownloadAll()
    {
        using var settings = new TestSettingsStore();
        var interaction = new TestDesktopInteractionContext();
        var viewModel = new DownloadSetterViewModel(
            interaction.Notifications,
            new StubFilePickerService(),
            settings.Store,
            new UnusedSubtitleTrackLoader(),
            NullLogger<DownloadSetterViewModel>.Instance)
        {
            DownloadAudio = false,
            DownloadVideo = false,
            DownloadDanmaku = false,
            DownloadSubtitle = false,
            DownloadCover = false
        };
        await using var viewModelLifetime = viewModel.ConfigureAwait(true);
        viewModel.OnDialogOpened(DownloadSettingsDialog.CreateRequest(
        [
            new(11, "zh", "中文", 0, "//zh"),
            new(22, "en", "English", 1, "//en")
        ]));

        viewModel.DownloadAudio = true;
        viewModel.ContentSelectionChangedCommand.Execute(null);
        viewModel.DownloadVideo = true;
        viewModel.ContentSelectionChangedCommand.Execute(null);
        viewModel.DownloadDanmaku = true;
        viewModel.ContentSelectionChangedCommand.Execute(null);
        viewModel.SubtitleTracks[0].IsSelected = true;
        viewModel.DownloadCover = true;
        viewModel.ContentSelectionChangedCommand.Execute(null);

        Assert.True(viewModel.DownloadAll);
        Assert.True(viewModel.SubtitleTracks[0].IsSelected);
        Assert.True(viewModel.SubtitleTracks[0].IsDefault);
        Assert.False(viewModel.SubtitleTracks[1].IsSelected);
    }

    [Fact]
    public async Task SelectingAvailableTrackDirectlyEnablesSubtitleDownload()
    {
        using var settings = new TestSettingsStore();
        var interaction = new TestDesktopInteractionContext();
        settings.Store.Update(current => current with
        {
            Video = current.Video with
            {
                Content = current.Video.Content with
                {
                    DownloadSubtitle = false
                }
            }
        });
        var viewModel = new DownloadSetterViewModel(
            interaction.Notifications,
            new StubFilePickerService(),
            settings.Store,
            new UnusedSubtitleTrackLoader(),
            NullLogger<DownloadSetterViewModel>.Instance);
        await using var viewModelLifetime = viewModel.ConfigureAwait(true);
        viewModel.OnDialogOpened(DownloadSettingsDialog.CreateRequest(
        [
            new(11, "zh", "中文", 0, "//zh")
        ]));

        var track = Assert.Single(viewModel.SubtitleTracks);
        track.IsSelected = true;

        Assert.True(viewModel.DownloadSubtitle);
        Assert.True(track.IsDefault);
        Assert.False(settings.Store.Current.Video.Content.DownloadSubtitle);

        track.IsSelected = false;

        Assert.False(viewModel.DownloadSubtitle);
        Assert.False(track.IsDefault);
        Assert.False(settings.Store.Current.Video.Content.DownloadSubtitle);
    }

    [Fact]
    public async Task DownloadCommandDoesNotReplaceDeselectedDefaultSubtitle()
    {
        using var settings = new TestSettingsStore();
        var interaction = new TestDesktopInteractionContext();
        var viewModel = new DownloadSetterViewModel(
            interaction.Notifications,
            new StubFilePickerService(),
            settings.Store,
            new UnusedSubtitleTrackLoader(),
            NullLogger<DownloadSetterViewModel>.Instance)
        {
            Directory = Path.GetTempPath(),
            DownloadSubtitle = true
        };
        await using var viewModelLifetime = viewModel.ConfigureAwait(true);
        viewModel.OnDialogOpened(DownloadSettingsDialog.CreateRequest(
        [
            new(11, "zh", "中文", 0, "//zh"),
            new(22, "en", "English", 1, "//en")
        ]));
        viewModel.SubtitleTracks[1].IsSelected = true;
        viewModel.SubtitleTracks[0].IsSelected = false;
        AppDialogResult? result = null;
        viewModel.CloseRequested += (_, value) => result = value;

        viewModel.DownloadCommand.Execute(null);

        var selection = DownloadSettingsDialog.DecodeResult(Assert.IsType<AppDialogResult>(result));
        Assert.True(selection.HasValue);
        Assert.Equal([22L], selection.Value.RequestedContent.SelectedSubtitleTrackIds.GetValueOrDefault().ToArray());
        Assert.Null(selection.Value.RequestedContent.DefaultSubtitleTrackId);
        Assert.DoesNotContain(viewModel.SubtitleTracks, track => track.IsDefault);
    }

    [Fact]
    public async Task LegacyEnabledSettingWithoutPreferenceStartsSafelyDisabled()
    {
        using var settings = new TestSettingsStore();
        settings.Store.Update(current => current with
        {
            Video = current.Video with
            {
                Content = current.Video.Content with
                {
                    DownloadSubtitle = true,
                    PreferredSubtitleLanguage = SubtitleLanguagePreference.Unspecified
                }
            }
        });
        var interaction = new TestDesktopInteractionContext();
        var viewModel = new DownloadSetterViewModel(
            interaction.Notifications,
            new StubFilePickerService(),
            settings.Store,
            new UnusedSubtitleTrackLoader(),
            NullLogger<DownloadSetterViewModel>.Instance);
        await using var viewModelLifetime = viewModel.ConfigureAwait(true);

        viewModel.OnDialogOpened(DownloadSettingsDialog.CreateRequest(
        [
            new(11, "zh", "中文", 0, "//zh")
        ]));

        Assert.False(viewModel.DownloadSubtitle);
        Assert.DoesNotContain(viewModel.SubtitleTracks, track => track.IsSelected);
    }

    [Fact]
    public async Task ConfiguredPreferencePreselectsOneTrackWithSameLanguageFallback()
    {
        using var settings = new TestSettingsStore();
        settings.Store.Update(current => current with
        {
            Video = current.Video with
            {
                Content = current.Video.Content with
                {
                    DownloadSubtitle = true,
                    PreferredSubtitleLanguage = SubtitleLanguagePreference.ChineseTraditional,
                    PreferredSubtitleType = SubtitleTypePreference.Standard
                }
            }
        });
        var interaction = new TestDesktopInteractionContext();
        var viewModel = new DownloadSetterViewModel(
            interaction.Notifications,
            new StubFilePickerService(),
            settings.Store,
            new UnusedSubtitleTrackLoader(),
            NullLogger<DownloadSetterViewModel>.Instance);
        await using var viewModelLifetime = viewModel.ConfigureAwait(true);

        viewModel.OnDialogOpened(DownloadSettingsDialog.CreateRequest(
        [
            new(11, "ai-zh-TW", "繁體 AI", 1, "//zh-tw-ai"),
            new(22, "en", "English", 0, "//en")
        ]));

        var selected = Assert.Single(viewModel.SubtitleTracks, track => track.IsSelected);
        Assert.Equal(11, selected.TrackId);
        Assert.True(selected.IsDefault);
    }

    [Fact]
    public async Task UndiscoveredTracksEncodeFallbackPreferenceWithoutSelectingAllTracks()
    {
        using var settings = new TestSettingsStore();
        var interaction = new TestDesktopInteractionContext();
        var viewModel = new DownloadSetterViewModel(
            interaction.Notifications,
            new StubFilePickerService(),
            settings.Store,
            new UnusedSubtitleTrackLoader(),
            NullLogger<DownloadSetterViewModel>.Instance)
        {
            Directory = Path.GetTempPath(),
            DownloadSubtitle = true
        };
        await using var viewModelLifetime = viewModel.ConfigureAwait(true);
        viewModel.OnDialogOpened(DownloadSettingsDialog.CreateRequest([]));
        AppDialogResult? result = null;
        viewModel.CloseRequested += (_, value) => result = value;

        viewModel.DownloadCommand.Execute(null);

        var selection = DownloadSettingsDialog.DecodeResult(Assert.IsType<AppDialogResult>(result));
        Assert.True(selection.HasValue);
        Assert.True(selection.Value.RequestedContent.Subtitle);
        Assert.Null(selection.Value.RequestedContent.SelectedSubtitleTrackIds);
        Assert.Equal(
            SubtitleLanguagePreference.ChineseSimplified,
            selection.Value.RequestedContent.PreferredSubtitleLanguage);
        Assert.Equal(
            SubtitleTypePreference.Standard,
            selection.Value.RequestedContent.PreferredSubtitleType);
    }

    [Fact]
    public async Task UndiscoveredTrackPreferenceChangesPersistForNextDialog()
    {
        using var settings = new TestSettingsStore();
        var interaction = new TestDesktopInteractionContext();
        var viewModel = new DownloadSetterViewModel(
            interaction.Notifications,
            new StubFilePickerService(),
            settings.Store,
            new UnusedSubtitleTrackLoader(),
            NullLogger<DownloadSetterViewModel>.Instance);
        await using var viewModelLifetime = viewModel.ConfigureAwait(true);
        viewModel.OnDialogOpened(DownloadSettingsDialog.CreateRequest([]));

        viewModel.DownloadSubtitle = true;
        viewModel.ContentSelectionChangedCommand.Execute(null);
        viewModel.SelectedSubtitleLanguagePreference = viewModel.SubtitleLanguagePreferences.Single(item =>
            item.Value == SubtitleLanguagePreference.English);
        viewModel.SelectedSubtitleTypePreference = viewModel.SubtitleTypePreferences.Single(item =>
            item.Value == SubtitleTypePreference.Ai);

        Assert.False(settings.Store.Current.Video.Content.DownloadSubtitle);
        Assert.NotEqual(SubtitleLanguagePreference.English,
            settings.Store.Current.Video.Content.PreferredSubtitleLanguage);

        viewModel.Directory = Path.GetTempPath();
        viewModel.DownloadCommand.Execute(null);

        var persistedContent = settings.Store.Current.Video.Content;
        Assert.True(persistedContent.DownloadSubtitle);
        Assert.Equal(SubtitleLanguagePreference.English, persistedContent.PreferredSubtitleLanguage);
        Assert.Equal(SubtitleTypePreference.Ai, persistedContent.PreferredSubtitleType);

        var nextViewModel = new DownloadSetterViewModel(
            interaction.Notifications,
            new StubFilePickerService(),
            settings.Store,
            new UnusedSubtitleTrackLoader(),
            NullLogger<DownloadSetterViewModel>.Instance);
        await using var nextViewModelLifetime = nextViewModel.ConfigureAwait(true);
        nextViewModel.OnDialogOpened(DownloadSettingsDialog.CreateRequest([]));

        Assert.True(nextViewModel.DownloadSubtitle);
        Assert.Equal(
            SubtitleLanguagePreference.English,
            nextViewModel.SelectedSubtitleLanguagePreference.Value);
        Assert.Equal(SubtitleTypePreference.Ai, nextViewModel.SelectedSubtitleTypePreference.Value);
    }

    [Fact]
    public async Task CancelDiscardsStagedContentAndSubtitlePreferences()
    {
        using var settings = new TestSettingsStore();
        var original = settings.Store.Current.Video;
        var interaction = new TestDesktopInteractionContext();
        var viewModel = new DownloadSetterViewModel(
            interaction.Notifications,
            new StubFilePickerService(),
            settings.Store,
            new UnusedSubtitleTrackLoader(),
            NullLogger<DownloadSetterViewModel>.Instance);
        await using var viewModelLifetime = viewModel.ConfigureAwait(true);
        viewModel.OnDialogOpened(DownloadSettingsDialog.CreateRequest([]));

        viewModel.DownloadSubtitle = true;
        viewModel.DownloadCover = !original.Content.DownloadCover;
        viewModel.ContentSelectionChangedCommand.Execute(null);
        viewModel.SelectedSubtitleLanguagePreference = viewModel.SubtitleLanguagePreferences.Single(item =>
            item.Value == SubtitleLanguagePreference.English);
        viewModel.SelectedSubtitleTypePreference = viewModel.SubtitleTypePreferences.Single(item =>
            item.Value == SubtitleTypePreference.Ai);
        viewModel.CloseCommand.Execute(null);

        Assert.Equal(original, settings.Store.Current.Video);
    }

    [Fact]
    public async Task CoverOnlyChangeDoesNotSaveDisplayedFallbackSubtitleDefaults()
    {
        using var settings = new TestSettingsStore();
        settings.Store.Update(current => current with
        {
            Video = current.Video with
            {
                Content = current.Video.Content with
                {
                    DownloadSubtitle = true,
                    PreferredSubtitleLanguage = SubtitleLanguagePreference.Unspecified,
                    PreferredSubtitleType = SubtitleTypePreference.Ai
                }
            }
        });
        var originalContent = settings.Store.Current.Video.Content;
        var interaction = new TestDesktopInteractionContext();
        var viewModel = new DownloadSetterViewModel(
            interaction.Notifications,
            new StubFilePickerService(),
            settings.Store,
            new UnusedSubtitleTrackLoader(),
            NullLogger<DownloadSetterViewModel>.Instance)
        {
            Directory = Path.GetTempPath()
        };
        await using var viewModelLifetime = viewModel.ConfigureAwait(true);
        viewModel.OnDialogOpened(DownloadSettingsDialog.CreateRequest([]));
        viewModel.DownloadCover = !viewModel.DownloadCover;
        viewModel.ContentSelectionChangedCommand.Execute(null);

        viewModel.DownloadCommand.Execute(null);

        var savedContent = settings.Store.Current.Video.Content;
        Assert.Equal(originalContent.DownloadSubtitle, savedContent.DownloadSubtitle);
        Assert.Equal(originalContent.PreferredSubtitleLanguage, savedContent.PreferredSubtitleLanguage);
        Assert.Equal(originalContent.PreferredSubtitleType, savedContent.PreferredSubtitleType);
        Assert.NotEqual(originalContent.DownloadCover, savedContent.DownloadCover);
    }

    [Fact]
    public async Task UnmatchedSubtitlePreferenceSkipsThisVideoWithoutChangingFuturePreference()
    {
        using var settings = new TestSettingsStore();
        settings.Store.Update(current => current with
        {
            Video = current.Video with
            {
                Content = current.Video.Content with
                {
                    DownloadSubtitle = true,
                    PreferredSubtitleLanguage = SubtitleLanguagePreference.ChineseSimplified
                }
            }
        });
        var interaction = new TestDesktopInteractionContext();
        var viewModel = new DownloadSetterViewModel(
            interaction.Notifications,
            new StubFilePickerService(),
            settings.Store,
            new UnusedSubtitleTrackLoader(),
            NullLogger<DownloadSetterViewModel>.Instance)
        {
            Directory = Path.GetTempPath()
        };
        await using var viewModelLifetime = viewModel.ConfigureAwait(true);
        viewModel.OnDialogOpened(DownloadSettingsDialog.CreateRequest(
        [
            new(22, "en", "English", 0, "//en")
        ]));

        Assert.True(viewModel.DownloadSubtitle);
        Assert.DoesNotContain(viewModel.SubtitleTracks, track => track.IsSelected);
        viewModel.DownloadCover = !viewModel.DownloadCover;
        viewModel.ContentSelectionChangedCommand.Execute(null);
        AppDialogResult? result = null;
        viewModel.CloseRequested += (_, value) => result = value;
        viewModel.DownloadCommand.Execute(null);

        var selection = DownloadSettingsDialog.DecodeResult(Assert.IsType<AppDialogResult>(result));
        Assert.True(selection.HasValue);
        Assert.False(selection.Value.RequestedContent.Subtitle);
        Assert.True(settings.Store.Current.Video.Content.DownloadSubtitle);
        Assert.Equal(SubtitleLanguagePreference.ChineseSimplified,
            settings.Store.Current.Video.Content.PreferredSubtitleLanguage);

        var nextViewModel = new DownloadSetterViewModel(
            interaction.Notifications,
            new StubFilePickerService(),
            settings.Store,
            new UnusedSubtitleTrackLoader(),
            NullLogger<DownloadSetterViewModel>.Instance);
        await using var nextViewModelLifetime = nextViewModel.ConfigureAwait(true);
        nextViewModel.OnDialogOpened(DownloadSettingsDialog.CreateRequest(
        [
            new(11, "zh", "中文", 0, "//zh")
        ]));
        Assert.True(Assert.Single(nextViewModel.SubtitleTracks).IsSelected);
    }

    [Fact]
    public void SubtitleTrackSelectionRaisesBindingNotification()
    {
        var track = new SubtitleTrackItem(
            new DownloadSettingsDialog.SubtitleTrack(11, "zh", "中文", 0, "//zh"),
            isSelected: true,
            isDefault: true);
        var changed = new List<string?>();
        track.PropertyChanged += (_, eventArgs) => changed.Add(eventArgs.PropertyName);

        track.IsSelected = false;

        Assert.Contains(nameof(track.IsSelected), changed);
        Assert.False(track.IsDefault);
    }

    private sealed class StubFilePickerService : IFilePickerService
    {
        public Task<string?> SelectFolderAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<string?> SelectVideoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> SelectVideosAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class UnusedSubtitleTrackLoader : ISubtitleTrackLoader
    {
        public Task<DownloadSettingsDialog.SubtitleTrackDiscovery> LoadAsync(
            DownloadSettingsDialog.SubtitlePage page, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
