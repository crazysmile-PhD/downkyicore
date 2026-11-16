using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DownKyi.Application.Desktop;
using DownKyi.Infrastructure.Settings;
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
            Width = 680,
            Height = 500,
            SizeToContent = SizeToContent.Manual,
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

            var contentOptions = Assert.IsType<WrapPanel>(
                view.FindControl<WrapPanel>("DownloadContentOptions"));
            var subtitleLabel = Assert.IsType<TextBlock>(
                view.FindControl<TextBlock>("SubtitleSectionLabel"));
            var subtitleBorder = Assert.IsType<Border>(
                view.FindControl<Border>("SubtitleTrackBorder"));
            var subtitleHelp = Assert.IsType<Button>(
                view.FindControl<Button>("SubtitleTrackHelpButton"));
            var subtitleHeading = Assert.Single(
                subtitleLabel.GetVisualAncestors().OfType<StackPanel>(),
                panel => panel.Children.Contains(subtitleHelp));
            var subtitleScrollViewer = Assert.IsType<ScrollViewer>(view.FindControl<ScrollViewer>("SubtitleTrackScrollViewer"));

            Assert.True(window.Bounds.Width <= 680);
            Assert.InRange(
                Math.Abs(directory.Bounds.Left - subtitleBorder.Bounds.Left),
                0,
                0.5);
            Assert.InRange(
                Math.Abs(contentOptions.Bounds.Left - subtitleBorder.Bounds.Left),
                0,
                0.5);
            Assert.True(subtitleHelp.IsVisible);
            Assert.IsType<Flyout>(subtitleHelp.Flyout);
            Assert.True(subtitleHeading.Bounds.Bottom <= subtitleBorder.Bounds.Top);
            var track = Assert.IsType<SubtitleTrackItem>(trackCheckbox.DataContext);
            var formScroll = view.FindControl<ScrollViewer>("DownloadOptionsScroll")!;
            formScroll.ScrollToEnd();
            window.UpdateLayout();
            var click = trackCheckbox.TranslatePoint(new Point(11, trackCheckbox.Bounds.Height / 2), window)!.Value;
            window.MouseDown(click, MouseButton.Left);
            window.MouseUp(click, MouseButton.Left);
            Assert.True(track.IsSelected);

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
            SizeToContent = SizeToContent.Manual,
            Content = view,
            DataContext = viewModel
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            var selector = Assert.IsType<ItemsControl>(
                view.FindControl<ItemsControl>("SubtitleTrackSelector"));
            var scrollViewer = Assert.IsType<ScrollViewer>(view.FindControl<ScrollViewer>("SubtitleTrackScrollViewer"));

            Assert.Equal(10, selector.Items.Count);
            Assert.True(scrollViewer.Bounds.Height <= 144);
            Assert.True(scrollViewer.Extent.Height > scrollViewer.Viewport.Height);

            var defaultOption = selector.GetVisualDescendants().OfType<RadioButton>().First();
            var optionRight = Assert.NotNull(defaultOption.TranslatePoint(
                new Point(defaultOption.Bounds.Width, 0), scrollViewer)).X;
            Assert.True(
                scrollViewer.Bounds.Width - optionRight >= 16,
                "The default-subtitle option must leave room for the overlay scrollbar.");

            scrollViewer.AllowAutoHide = false; // Force Avalonia's expanded scrollbar state.
            window.UpdateLayout();
            var scrollBar = Assert.Single(
                scrollViewer.GetVisualDescendants().OfType<ScrollBar>(),
                bar => bar.Orientation == Avalonia.Layout.Orientation.Vertical);
            Assert.True(scrollBar.IsExpanded);
            var track = Assert.Single(
                scrollBar.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Rectangle>(),
                rectangle => rectangle.Name == "TrackRect");
            Assert.Equal(0, track.Opacity);
            var thumb = Assert.Single(scrollBar.GetVisualDescendants().OfType<Thumb>());
            Assert.InRange(thumb.RenderTransform!.Value.M11, 0.124, 0.126);
            Assert.All(
                scrollBar.GetVisualDescendants().OfType<RepeatButton>()
                    .Where(button => button.Name is "PART_LineUpButton" or "PART_LineDownButton"),
                button => Assert.Equal(0, button.Opacity));
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
        var window = new DialogWindow { Content = view, DataContext = viewModel, Width = 480, Height = 420, SizeToContent = SizeToContent.Manual };

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

    [AvaloniaFact]
    public void ResizingKeepsActionsVisibleAndSubtitleSelectionIntact()
    {
        var application = DesktopTestResources.EnsureProductThemeResources();
        var fluentTheme = new FluentTheme();
        application.Styles.Insert(0, fluentTheme);
        using var fixture = new ViewModelFixture(downloadSubtitle: false);
        var viewModel = fixture.CreateViewModel();
        viewModel.OnDialogOpened(CreateRequest(subtitleCount: 10));
        var longDirectory = "C:\\Videos\\" + string.Join("\\", Enumerable.Repeat("LongDownloadDirectory", 12));
        viewModel.DirectoryList.Add(longDirectory);
        viewModel.Directory = longDirectory;
        var view = new ViewDownloadSetter { DataContext = viewModel };
        var window = new DialogWindow { Content = view, SizeToContent = SizeToContent.Manual, Width = 680, Height = 640 };
        try
        {
            window.Show();
            var selected = viewModel.SubtitleTracks[0];
            selected.IsSelected = true;
            selected.IsDefault = true;
            var scroll = view.FindControl<ScrollViewer>("DownloadOptionsScroll")!;
            var footer = view.FindControl<StackPanel>("DownloadOptionsActions")!;
            foreach (var size in new[] { new Size(480, 360), new Size(680, 518), new Size(960, 800), new Size(480, 420) })
            {
                window.Width = size.Width;
                window.Height = size.Height;
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                foreach (var offset in new[] { Vector.Zero, new Vector(0, double.MaxValue) })
                {
                    scroll.Offset = offset;
                    window.UpdateLayout();
                    var footerTop = footer.TranslatePoint(default, view)!.Value.Y;
                    Assert.True(scroll.Bounds.Bottom <= footerTop);
                    Assert.True(footerTop + footer.Bounds.Height <= view.Bounds.Height);
                    Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1);
                    foreach (var button in footer.Children.OfType<Button>())
                    {
                        var position = button.TranslatePoint(default, view)!.Value;
                        Assert.InRange(position.X, 0, view.Bounds.Width - button.Bounds.Width);
                        Assert.True(button.IsEffectivelyEnabled);
                        Assert.True(button.Bounds.Height >= 40);
                    }
                    Assert.True(selected.IsSelected);
                    Assert.True(selected.IsDefault);
                }
            }
        }
        finally
        {
            window.Close();
            application.Styles.Remove(fluentTheme);
        }
    }

    [Theory]
    [InlineData(1920, 1080, 1, 1440, 900, 680, 640)]
    [InlineData(1920, 1080, 1.5, 800, 550, 680, 518)]
    [InlineData(1280, 720, 2, 800, 550, 608, 328)]
    public void DownloadOptionsFitTheOwnerAndScaledWorkingArea(int width, int height, double scaling, double ownerWidth, double ownerHeight, double expectedWidth, double expectedHeight)
    {
        var size = AvaloniaDialogService.ConstrainDialogSize(AppDialog.DownloadSettings, new PixelRect(0, 0, width, height), scaling, new Size(ownerWidth, ownerHeight));
        Assert.Equal(new Size(expectedWidth, expectedHeight), size);
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

        public DownloadSetterViewModel CreateViewModel()
        {
            DesktopTestResources.EnsureDownloadProjectionResources();
            return new(
                new NotificationServiceStub(),
                new FilePickerServiceStub(),
                Settings,
                NullLogger<DownloadSetterViewModel>.Instance);
        }

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
