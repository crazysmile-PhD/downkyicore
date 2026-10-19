using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using DownKyi.Application.Desktop;
using DownKyi.Core.Settings;
using DownKyi.Platform;
using DownKyi.Services.Download;
using DownKyi.ViewModels.Dialogs;
using DownKyi.Views.Dialogs;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Desktop.Tests;

public sealed class DownloadSettingsDialogLayoutTests
{
    [AvaloniaFact]
    public void AvailableSubtitleCheckboxRemainsClickableAndSectionsDoNotOverlap()
    {
        var application = DesktopTestResources.EnsureDownloadProjectionResources();
        DesktopTestResources.EnsureProductThemeResources();
        var fluentTheme = new FluentTheme();
        application.Styles.Insert(0, fluentTheme);
        using var fixture = new ViewModelFixture(downloadSubtitle: false);
        var viewModel = fixture.CreateViewModel();
        viewModel.OnDialogOpened(CreateRequest());
        var view = new ViewDownloadSetter { DataContext = viewModel };
        var window = new DialogWindow
        {
            Content = view,
            DataContext = viewModel
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            var selector = Assert.IsType<ItemsControl>(
                view.FindControl<ItemsControl>("SubtitleTrackSelector"));
            var trackCheckbox = Assert.Single(
                selector.GetVisualDescendants().OfType<CheckBox>(),
                checkbox => checkbox.DataContext is SubtitleTrackItem);
            Assert.True(trackCheckbox.IsEnabled);

            var directory = Assert.IsType<ComboBox>(
                view.FindControl<ComboBox>("DirectoryComboBox"));
            Assert.True(directory.Bounds.Width >= 360);

            var contentSection = Assert.IsType<Grid>(
                view.FindControl<Grid>("DownloadContentSection"));
            Assert.Equal(5, contentSection.GetVisualDescendants().OfType<CheckBox>().Count());

            var contentOptions = Assert.IsType<Grid>(
                view.FindControl<Grid>("DownloadContentOptions"));
            var subtitleLabel = Assert.IsType<TextBlock>(
                view.FindControl<TextBlock>("SubtitleSectionLabel"));
            var subtitleBorder = Assert.IsType<Border>(
                view.FindControl<Border>("SubtitleTrackBorder"));
            var subtitleTip = Assert.IsType<TextBlock>(
                view.FindControl<TextBlock>("SubtitleTrackSelectionTip"));
            var subtitleScrollViewer = Assert.Single(
                selector.GetVisualAncestors().OfType<ScrollViewer>());

            Assert.InRange(
                Math.Abs(directory.Bounds.Left - subtitleBorder.Bounds.Left),
                0,
                0.5);
            Assert.InRange(
                Math.Abs(contentOptions.Bounds.Left - subtitleBorder.Bounds.Left),
                0,
                0.5);
            Assert.InRange(
                Math.Abs(subtitleLabel.Bounds.Center.Y - subtitleBorder.Bounds.Center.Y),
                0,
                0.5);
            Assert.True(subtitleTip.IsVisible);
            Assert.Equal(TextWrapping.Wrap, subtitleTip.TextWrapping);
            Assert.Contains("并非重复下载", subtitleTip.Text, StringComparison.Ordinal);
            Assert.Contains("请保留同名 .srt", subtitleTip.Text, StringComparison.Ordinal);
            Assert.True(subtitleScrollViewer.Bounds.Bottom <= subtitleTip.Bounds.Top);

            AssertAppearsBelow(view, "TransferIllustration", "DirectorySection");
            AssertAppearsBelow(view, "DirectorySection", "DownloadContentSection");
            AssertAppearsBelow(view, "DownloadContentSection", "SubtitleSection");
        }
        finally
        {
            window.Close();
            application.Styles.Remove(fluentTheme);
        }
    }

    [AvaloniaFact]
    public void ManySubtitleTracksStayWithinTheScrollableSelector()
    {
        var application = DesktopTestResources.EnsureProductThemeResources();
        var fluentTheme = new FluentTheme();
        application.Styles.Insert(0, fluentTheme);
        using var fixture = new ViewModelFixture(downloadSubtitle: true);
        var viewModel = fixture.CreateViewModel();
        viewModel.OnDialogOpened(CreateRequest(subtitleCount: 10));
        var view = new ViewDownloadSetter { DataContext = viewModel };
        var window = new DialogWindow
        {
            Width = 670,
            Height = 500,
            Content = view,
            DataContext = viewModel
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            var selector = Assert.IsType<ItemsControl>(
                view.FindControl<ItemsControl>("SubtitleTrackSelector"));
            var scrollViewer = Assert.Single(
                selector.GetVisualAncestors().OfType<ScrollViewer>());

            Assert.Equal(10, selector.Items.Count);
            Assert.True(scrollViewer.Bounds.Height <= 144);
            Assert.True(scrollViewer.Extent.Height > scrollViewer.Viewport.Height);
        }
        finally
        {
            window.Close();
            application.Styles.Remove(fluentTheme);
        }
    }

    [AvaloniaFact]
    public void MissingTrackListKeepsFallbackSubtitleControlsVisible()
    {
        var application = DesktopTestResources.EnsureProductThemeResources();
        var fluentTheme = new FluentTheme();
        application.Styles.Insert(0, fluentTheme);
        using var fixture = new ViewModelFixture(downloadSubtitle: false);
        var viewModel = fixture.CreateViewModel();
        viewModel.OnDialogOpened(DownloadSettingsDialog.CreateRequest([]));
        var view = new ViewDownloadSetter { DataContext = viewModel };
        var window = new DialogWindow { Content = view, DataContext = viewModel };

        try
        {
            window.Show();
            window.UpdateLayout();

            var fallback = Assert.IsType<Border>(
                view.FindControl<Border>("SubtitleFallbackPanel"));
            Assert.True(fallback.IsVisible);
            Assert.Single(fallback.GetVisualDescendants().OfType<CheckBox>());
            Assert.Equal(2, fallback.GetVisualDescendants().OfType<ComboBox>().Count());
            AssertAppearsBelow(view, "DownloadContentSection", "SubtitleSection");
        }
        finally
        {
            window.Close();
            application.Styles.Remove(fluentTheme);
        }
    }

    private static void AssertAppearsBelow(Control view, string upperName, string lowerName)
    {
        var upper = Assert.IsAssignableFrom<Control>(view.FindControl<Control>(upperName));
        var lower = Assert.IsAssignableFrom<Control>(view.FindControl<Control>(lowerName));
        Assert.True(
            upper.Bounds.Bottom <= lower.Bounds.Top,
            $"{upperName} overlaps {lowerName}.");
    }

    private static AppDialogRequest CreateRequest(int subtitleCount = 1) =>
        DownloadSettingsDialog.CreateRequest(
            new[]
            {
                (Language: "zh-CN", DisplayLanguage: "中文（中国）", Type: 0),
                (Language: "ai-zh", DisplayLanguage: "中文（AI）", Type: 1),
                (Language: "en-US", DisplayLanguage: "English (US)", Type: 0),
                (Language: "ai-en", DisplayLanguage: "English (AI)", Type: 1),
                (Language: "ja-JP", DisplayLanguage: "日本語", Type: 0),
                (Language: "ai-ja", DisplayLanguage: "日本語（AI）", Type: 1),
                (Language: "ko-KR", DisplayLanguage: "한국어", Type: 0),
                (Language: "ai-ko", DisplayLanguage: "한국어（AI）", Type: 1),
                (Language: "zh-TW", DisplayLanguage: "中文（繁體）", Type: 0),
                (Language: "ai-zh-TW", DisplayLanguage: "中文（繁體 AI）", Type: 1)
            }
            .Take(subtitleCount)
            .Select((track, index) => new DownloadSettingsDialog.SubtitleTrack(
                index + 1,
                track.Language,
                track.DisplayLanguage,
                track.Type,
                $"https://example.invalid/subtitle-{index + 1}.json"))
            .ToArray());

    private sealed class ViewModelFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-download-settings-{Guid.NewGuid():N}");

        public ViewModelFixture(bool downloadSubtitle)
        {
            Directory.CreateDirectory(_directory);
            Settings = new SettingsStore(Path.Combine(_directory, "settings.json"));
            Settings.Update(settings => settings with
            {
                Video = settings.Video with
                {
                    SaveVideoRootPath = _directory,
                    HistoryVideoRootPaths = [_directory],
                    Content = settings.Video.Content with
                    {
                        DownloadSubtitle = downloadSubtitle,
                        PreferredSubtitleLanguage = downloadSubtitle
                            ? Domain.Downloads.SubtitleLanguagePreference.ChineseSimplified
                            : Domain.Downloads.SubtitleLanguagePreference.Unspecified
                    }
                }
            });
        }

        public SettingsStore Settings { get; }

        public ViewDownloadSetterViewModel CreateViewModel() =>
            new(
                new NotificationServiceStub(),
                new FilePickerServiceStub(),
                Settings,
                NullLogger<ViewDownloadSetterViewModel>.Instance);

        public void Dispose()
        {
            Settings.Dispose();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }

    private sealed class NotificationServiceStub : IUserNotificationService
    {
        public event EventHandler<UserNotificationEventArgs>? NotificationRaised
        {
            add { }
            remove { }
        }

        public void Show(string message)
        {
        }
    }

    private sealed class FilePickerServiceStub : IFilePickerService
    {
        public Task<string?> SelectFolderAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<string?> SelectVideoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> SelectVideosAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
