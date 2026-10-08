using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using DownKyi.Models;
using DownKyi.Views.Settings;

namespace DownKyi.Desktop.Tests;

public sealed class SettingsComboBoxLayoutTests
{
    private static readonly string[] AriaLogLevels = ["debug", "info", "warn"];
    private static readonly string[] AriaFileAllocations =
        ["PREALLOC（预分配磁盘空间，避免文件碎片）"];
    private static readonly string[] FileNameTimeFormats = ["yyyy-MM-dd", "yyyy.MM.dd"];
    private static readonly OrderFormatDisplay[] OrderFormats =
    [
        new() { Name = "前导零填充" }
    ];

    [AvaloniaFact]
    public Task NetworkProxyChoicesUseUnframedVerticalGroupAcrossThemes()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            var application = DesktopTestResources.EnsureProductThemeResources();
            var originalTheme = application.RequestedThemeVariant;
            var fluentTheme = new FluentTheme();
            application.Styles.Insert(0, fluentTheme);

            try
            {
                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    application.RequestedThemeVariant = theme;
                    AssertNetworkProxyGroup(application, theme);
                }
            }
            finally
            {
                application.RequestedThemeVariant = originalTheme;
                application.Styles.Remove(fluentTheme);
            }
        });
    }

    [AvaloniaFact]
    public Task NetworkSettingsPageUsesWhitespaceAndSectionHierarchyAcrossThemes()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            var application = DesktopTestResources.EnsureProductThemeResources();
            var originalTheme = application.RequestedThemeVariant;
            var fluentTheme = new FluentTheme();
            application.Styles.Insert(0, fluentTheme);

            try
            {
                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    application.RequestedThemeVariant = theme;
                    AssertGeneralNetworkHierarchy(application, theme);
                    AssertAriaNetworkHierarchy(application, theme);
                    AssertCustomAriaHierarchy(application, theme);
                }
            }
            finally
            {
                application.RequestedThemeVariant = originalTheme;
                application.Styles.Remove(fluentTheme);
            }
        });
    }

    [AvaloniaFact]
    public Task DescriptiveChoicesCanGrowBeyondTheirBaselineWidth()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            var application = DesktopTestResources.EnsureProductThemeResources();
            var originalTheme = application.RequestedThemeVariant;
            var fluentTheme = new FluentTheme();
            application.Styles.Insert(0, fluentTheme);

            try
            {
                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    application.RequestedThemeVariant = theme;
                    AssertChoiceGrows(
                        CreateVisibleAriaSettings(),
                        "NameAriaFileAllocations",
                        AriaFileAllocations,
                        180);
                }
            }
            finally
            {
                application.RequestedThemeVariant = originalTheme;
                application.Styles.Remove(fluentTheme);
            }
        });
    }

    [AvaloniaFact]
    public Task VideoSettingsUpperFormUsesAlignedGroupsAcrossThemes()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            var application = DesktopTestResources.EnsureProductThemeResources();
            var originalTheme = application.RequestedThemeVariant;
            var fluentTheme = new FluentTheme();
            application.Styles.Insert(0, fluentTheme);

            try
            {
                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    application.RequestedThemeVariant = theme;
                    AssertVideoSettingsUpperForm(application, theme);
                }
            }
            finally
            {
                application.RequestedThemeVariant = originalTheme;
                application.Styles.Remove(fluentTheme);
            }
        });
    }

    [AvaloniaFact]
    public Task DanmakuSettingsUsesLightweightGroupsAcrossThemes()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            var application = DesktopTestResources.EnsureProductThemeResources();
            var originalTheme = application.RequestedThemeVariant;
            var fluentTheme = new FluentTheme();
            application.Styles.Insert(0, fluentTheme);

            try
            {
                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    application.RequestedThemeVariant = theme;
                    AssertDanmakuSettingsHierarchy(application, theme);
                }
            }
            finally
            {
                application.RequestedThemeVariant = originalTheme;
                application.Styles.Remove(fluentTheme);
            }
        });
    }

    private static void AssertDanmakuSettingsHierarchy(
        Avalonia.Application application,
        ThemeVariant theme)
    {
        var view = new ViewDanmaku();
        var pageTitle = Assert.IsType<TextBlock>(
            view.FindControl<TextBlock>("NameDanmakuPageTitle"));
        var filterSection = Assert.IsType<StackPanel>(
            view.FindControl<StackPanel>("NameDanmakuFilterSection"));
        var filterTitle = Assert.IsType<TextBlock>(
            view.FindControl<TextBlock>("NameDanmakuFilterTitle"));
        var keywordList = Assert.IsType<TextBox>(
            view.FindControl<TextBox>("NameBlockedDanmakuKeywords"));
        var senderList = Assert.IsType<TextBox>(
            view.FindControl<TextBox>("NameBlockedDanmakuSenderUids"));
        var saveButton = Assert.IsType<Button>(
            view.FindControl<Button>("NameSaveDanmakuBlacklistsButton"));
        var displayTitle = Assert.IsType<TextBlock>(
            view.FindControl<TextBlock>("NameDanmakuDisplaySettingsTitle"));
        var displayGrid = Assert.IsType<Grid>(
            view.FindControl<Grid>("NameDanmakuDisplaySettingsGrid"));
        var screenWidth = Assert.IsType<TextBox>(
            view.FindControl<TextBox>("NameScreenWidth"));
        var font = Assert.IsType<ComboBox>(
            view.FindControl<ComboBox>("NameFonts"));
        var fontSize = Assert.IsType<TextBox>(
            view.FindControl<TextBox>("NameFontSize"));
        var lineCount = Assert.IsType<TextBox>(
            view.FindControl<TextBox>("NameLineCount"));
        var sync = Assert.IsType<CheckBox>(
            view.FindControl<CheckBox>("NameLayoutAlgorithmSync"));
        pageTitle.Text = "弹幕";
        filterTitle.Text = "屏蔽规则";
        displayTitle.Text = "显示设置";
        saveButton.Content = "保存黑名单";
        font.ItemsSource = new[] { "Microsoft YaHei UI", "Arial" };
        font.SelectedIndex = 0;
        var window = new Window
        {
            Content = view,
            Width = 840,
            Height = 780
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            var pageTitleOrigin = Assert.NotNull(pageTitle.TranslatePoint(default, window));
            var filterTitleOrigin = Assert.NotNull(filterTitle.TranslatePoint(default, window));
            var keywordOrigin = Assert.NotNull(keywordList.TranslatePoint(default, window));
            var senderOrigin = Assert.NotNull(senderList.TranslatePoint(default, window));
            var saveOrigin = Assert.NotNull(saveButton.TranslatePoint(default, window));
            var displayTitleOrigin = Assert.NotNull(displayTitle.TranslatePoint(default, window));
            var displayGridOrigin = Assert.NotNull(displayGrid.TranslatePoint(default, window));
            var screenWidthOrigin = Assert.NotNull(screenWidth.TranslatePoint(default, window));
            var fontOrigin = Assert.NotNull(font.TranslatePoint(default, window));
            var fontSizeOrigin = Assert.NotNull(fontSize.TranslatePoint(default, window));
            var lineCountOrigin = Assert.NotNull(lineCount.TranslatePoint(default, window));
            var syncOrigin = Assert.NotNull(sync.TranslatePoint(default, window));

            Assert.Null(filterSection.Background);
            Assert.Equal(600, filterSection.Bounds.Width, 0.5);
            Assert.Equal(keywordOrigin.X, senderOrigin.X, 0.5);
            Assert.Equal(keywordList.Bounds.Width, senderList.Bounds.Width, 0.5);
            Assert.True(keywordList.Bounds.Width >= 600);
            Assert.True(filterTitleOrigin.Y > pageTitleOrigin.Y + pageTitle.Bounds.Height);
            Assert.True(senderOrigin.Y > keywordOrigin.Y + keywordList.Bounds.Height);
            Assert.True(saveOrigin.Y > senderOrigin.Y + senderList.Bounds.Height);
            Assert.True(displayTitleOrigin.Y > saveOrigin.Y + saveButton.Bounds.Height);
            Assert.True(displayGridOrigin.Y > displayTitleOrigin.Y + displayTitle.Bounds.Height);
            Assert.Equal(screenWidthOrigin.X, fontOrigin.X, 0.5);
            Assert.Equal(fontOrigin.X, fontSizeOrigin.X, 0.5);
            Assert.Equal(fontSizeOrigin.X, lineCountOrigin.X, 0.5);
            Assert.Equal(lineCountOrigin.X, syncOrigin.X, 0.5);
            Assert.Equal(320, font.Bounds.Width, 0.5);
            Assert.Equal(120, fontSize.Bounds.Width, 0.5);
            Assert.Equal(120, lineCount.Bounds.Width, 0.5);
            Assert.Equal(
                ResourceColor(application, "BrushTextPrimary", theme),
                SolidColor(filterTitle.Foreground));
            Assert.Equal(
                ResourceColor(application, "BrushTextPrimary", theme),
                SolidColor(displayTitle.Foreground));
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertVideoSettingsUpperForm(
        Avalonia.Application application,
        ThemeVariant theme)
    {
        var view = new ViewVideo();
        var pageTitle = Assert.IsType<TextBlock>(
            view.FindControl<TextBlock>("NameVideoPageTitle"));
        var preferencesTitle = Assert.IsType<TextBlock>(
            view.FindControl<TextBlock>("NameVideoDownloadPreferencesTitle"));
        var postProcessingTitle = Assert.IsType<TextBlock>(
            view.FindControl<TextBlock>("NameVideoPostProcessingTitle"));
        var ffmpegTitle = Assert.IsType<TextBlock>(
            view.FindControl<TextBlock>("NameVideoFfmpegTitle"));
        var videoCodec = Assert.IsType<ComboBox>(
            view.FindControl<ComboBox>("NameVideoCodecs"));
        var videoQuality = Assert.IsType<ComboBox>(
            view.FindControl<ComboBox>("NameVideoQualityList"));
        var audioQuality = Assert.IsType<ComboBox>(
            view.FindControl<ComboBox>("NameAudioQualityList"));
        var strategy = Assert.IsType<TextBlock>(
            view.FindControl<TextBlock>("NameVideoParseStrategy"));
        var transcodeVideo = Assert.IsType<CheckBox>(
            view.FindControl<CheckBox>("NameTranscodeFlvToMp4"));
        var transcodeAudio = Assert.IsType<CheckBox>(
            view.FindControl<CheckBox>("NameTranscodeAacToMp3"));
        var hardwareAcceleration = Assert.IsType<ComboBox>(
            view.FindControl<ComboBox>("NameFfmpegHardwareAcceleration"));
        var parallelJobs = Assert.IsType<ComboBox>(
            view.FindControl<ComboBox>("NameFfmpegMaxParallelJobs"));
        pageTitle.Text = "视频";
        preferencesTitle.Text = "下载偏好";
        postProcessingTitle.Text = "下载后处理";
        ffmpegTitle.Text = "FFmpeg";
        strategy.Text = "網站優先，缺少所需媒體或品質時由 API 補足。";
        transcodeVideo.Content = "下载FLV视频后转码为mp4";
        transcodeAudio.Content = "下载AAC音频后转码为mp3";
        var window = new Window
        {
            Content = view,
            Width = 840,
            Height = 680
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            var pageTitleOrigin = Assert.NotNull(pageTitle.TranslatePoint(default, window));
            var preferencesTitleOrigin = Assert.NotNull(
                preferencesTitle.TranslatePoint(default, window));
            var postProcessingTitleOrigin = Assert.NotNull(
                postProcessingTitle.TranslatePoint(default, window));
            var ffmpegTitleOrigin = Assert.NotNull(
                ffmpegTitle.TranslatePoint(default, window));
            var videoCodecOrigin = Assert.NotNull(videoCodec.TranslatePoint(default, window));
            var videoQualityOrigin = Assert.NotNull(videoQuality.TranslatePoint(default, window));
            var audioQualityOrigin = Assert.NotNull(audioQuality.TranslatePoint(default, window));
            var strategyOrigin = Assert.NotNull(strategy.TranslatePoint(default, window));
            var transcodeVideoOrigin = Assert.NotNull(
                transcodeVideo.TranslatePoint(default, window));
            var transcodeAudioOrigin = Assert.NotNull(
                transcodeAudio.TranslatePoint(default, window));
            var hardwareOrigin = Assert.NotNull(
                hardwareAcceleration.TranslatePoint(default, window));
            var parallelOrigin = Assert.NotNull(parallelJobs.TranslatePoint(default, window));

            Assert.Contains("網站優先", strategy.Text, StringComparison.Ordinal);
            Assert.Equal(videoCodecOrigin.X, videoQualityOrigin.X, 0.5);
            Assert.Equal(videoQualityOrigin.X, audioQualityOrigin.X, 0.5);
            Assert.Equal(audioQualityOrigin.X, strategyOrigin.X, 0.5);
            Assert.True(videoCodec.Bounds.Width >= 320);
            Assert.Equal(videoCodec.Bounds.Width, videoQuality.Bounds.Width, 0.5);
            Assert.Equal(videoQuality.Bounds.Width, audioQuality.Bounds.Width, 0.5);
            Assert.True(strategy.Bounds.Width <= audioQuality.Bounds.Width);
            Assert.True(preferencesTitleOrigin.Y > pageTitleOrigin.Y + pageTitle.Bounds.Height);
            Assert.True(postProcessingTitleOrigin.Y > strategyOrigin.Y + strategy.Bounds.Height);
            Assert.Equal(transcodeVideoOrigin.X, transcodeAudioOrigin.X, 0.5);
            Assert.True(
                transcodeAudioOrigin.Y > transcodeVideoOrigin.Y + transcodeVideo.Bounds.Height);
            Assert.True(ffmpegTitleOrigin.Y > transcodeAudioOrigin.Y + transcodeAudio.Bounds.Height);
            Assert.Equal(hardwareOrigin.X, parallelOrigin.X, 0.5);
            Assert.True(hardwareAcceleration.Bounds.Width >= 320);
            Assert.Equal(120, parallelJobs.Bounds.Width, 0.5);
            Assert.DoesNotContain(
                view.GetVisualDescendants().OfType<TextBlock>(),
                candidate => candidate.Height == 1 && candidate.Background is not null);
            Assert.Equal(
                ResourceColor(application, "BrushTextPrimary", theme),
                SolidColor(preferencesTitle.Foreground));
            Assert.Equal(
                ResourceColor(application, "BrushTextPrimary", theme),
                SolidColor(postProcessingTitle.Foreground));
            Assert.Equal(
                ResourceColor(application, "BrushTextPrimary", theme),
                SolidColor(ffmpegTitle.Foreground));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public Task VideoFileNameEditorKeepsValuesAndActionsReadableAcrossThemes()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            var application = DesktopTestResources.EnsureProductThemeResources();
            var originalTheme = application.RequestedThemeVariant;
            var fluentTheme = new FluentTheme();
            application.Styles.Insert(0, fluentTheme);

            try
            {
                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    application.RequestedThemeVariant = theme;
                    AssertVideoFileNameEditorLayout();
                }
            }
            finally
            {
                application.RequestedThemeVariant = originalTheme;
                application.Styles.Remove(fluentTheme);
            }
        });
    }

    private static void AssertVideoFileNameEditorLayout()
    {
        var view = new ViewVideo();
        var options = Assert.IsType<Grid>(
            view.FindControl<Grid>("NameFileNameEditorOptions"));
        var timeLabel = Assert.IsType<TextBlock>(
            view.FindControl<TextBlock>("NameFileNamePartTimeFormatLabel"));
        var timeFormat = Assert.IsType<ComboBox>(
            view.FindControl<ComboBox>("NameFileNamePartTimeFormat"));
        var orderLabel = Assert.IsType<TextBlock>(
            view.FindControl<TextBlock>("NameOrderFormatLabel"));
        var orderFormat = Assert.IsType<ComboBox>(
            view.FindControl<ComboBox>("NameOrderFormat"));
        var reset = Assert.IsType<Button>(
            view.FindControl<Button>("NameResetFileNameButton"));
        timeLabel.Text = "时间格式：";
        orderLabel.Text = "序号格式：";
        reset.Content = "恢复默认";
        timeFormat.ItemsSource = FileNameTimeFormats;
        timeFormat.SelectedIndex = 0;
        orderFormat.ItemsSource = OrderFormats;
        orderFormat.SelectedIndex = 0;
        var window = new Window
        {
            Content = view,
            Width = 840,
            Height = 680
        };

        try
        {
            window.Show();
            window.UpdateLayout();
            var scroll = Assert.IsType<ScrollViewer>(view.Content);
            scroll.Offset = new Vector(
                0,
                Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
            window.UpdateLayout();

            var optionsOrigin = Assert.NotNull(options.TranslatePoint(default, window));
            var timeLabelOrigin = Assert.NotNull(timeLabel.TranslatePoint(default, window));
            var timeOrigin = Assert.NotNull(timeFormat.TranslatePoint(default, window));
            var orderLabelOrigin = Assert.NotNull(orderLabel.TranslatePoint(default, window));
            var orderOrigin = Assert.NotNull(orderFormat.TranslatePoint(default, window));
            var resetOrigin = Assert.NotNull(reset.TranslatePoint(default, window));

            Assert.Equal("yyyy-MM-dd", timeFormat.SelectedItem);
            Assert.Equal("恢复默认", reset.Content);
            Assert.True(timeFormat.Bounds.Width >= 140);
            Assert.True(orderFormat.Bounds.Width >= 140);
            Assert.True(reset.Bounds.Width >= 92);
            Assert.True(timeLabelOrigin.X < timeOrigin.X);
            Assert.True(timeOrigin.X + timeFormat.Bounds.Width < orderLabelOrigin.X);
            Assert.True(orderLabelOrigin.X < orderOrigin.X);
            Assert.True(orderOrigin.X + orderFormat.Bounds.Width < resetOrigin.X);
            Assert.True(
                resetOrigin.X + reset.Bounds.Width <=
                optionsOrigin.X + options.Bounds.Width + 0.5);
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertNetworkProxyGroup(
        Avalonia.Application application,
        ThemeVariant theme)
    {
        var view = new NetworkGeneralSettingsView();
        var section = Assert.IsType<StackPanel>(
            view.FindControl<StackPanel>("NameNetworkProxySection"));
        var choices = Assert.IsType<StackPanel>(
            view.FindControl<StackPanel>("NameNetworkProxyChoices"));
        var title = Assert.IsType<TextBlock>(
            view.FindControl<TextBlock>("NameNetworkProxyTitle"));
        var none = Assert.IsType<RadioButton>(
            view.FindControl<RadioButton>("NameNetworkProxyNone"));
        var system = Assert.IsType<RadioButton>(
            view.FindControl<RadioButton>("NameNetworkProxySystem"));
        var custom = Assert.IsType<RadioButton>(
            view.FindControl<RadioButton>("NameNetworkProxyCustom"));
        var window = new Window
        {
            Content = view,
            Width = 720,
            Height = 460
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            var titleOrigin = Assert.NotNull(title.TranslatePoint(default, window));
            var noneOrigin = Assert.NotNull(none.TranslatePoint(default, window));
            var systemOrigin = Assert.NotNull(system.TranslatePoint(default, window));
            var customOrigin = Assert.NotNull(custom.TranslatePoint(default, window));

            Assert.Same(title, section.Children[0]);
            Assert.Same(choices, section.Children[1]);
            Assert.Equal(noneOrigin.X, systemOrigin.X, 0.5);
            Assert.Equal(systemOrigin.X, customOrigin.X, 0.5);
            Assert.True(noneOrigin.Y > titleOrigin.Y + title.Bounds.Height);
            Assert.True(systemOrigin.Y > noneOrigin.Y + none.Bounds.Height);
            Assert.True(customOrigin.Y > systemOrigin.Y + system.Bounds.Height);
            Assert.Equal(
                ResourceColor(application, "BrushTextPrimary", theme),
                SolidColor(title.Foreground));
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertGeneralNetworkHierarchy(
        Avalonia.Application application,
        ThemeVariant theme)
    {
        var view = new NetworkGeneralSettingsView();
        var pageTitle = Assert.IsType<TextBlock>(
            view.FindControl<TextBlock>("NameNetworkPageTitle"));
        var connectionTitle = Assert.IsType<TextBlock>(
            view.FindControl<TextBlock>("NameNetworkConnectionTitle"));
        var downloaderTitle = Assert.IsType<TextBlock>(
            view.FindControl<TextBlock>("NameDownloaderTitle"));
        var highSpeed = Assert.IsType<CheckBox>(
            view.FindControl<CheckBox>("NameHighSpeedDownloadMode"));
        var window = new Window
        {
            Content = view,
            Width = 760,
            Height = 680
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            var pageOrigin = Assert.NotNull(pageTitle.TranslatePoint(default, window));
            var connectionOrigin = Assert.NotNull(connectionTitle.TranslatePoint(default, window));
            var downloaderOrigin = Assert.NotNull(downloaderTitle.TranslatePoint(default, window));
            var highSpeedOrigin = Assert.NotNull(highSpeed.TranslatePoint(default, window));

            Assert.True(connectionOrigin.Y > pageOrigin.Y + pageTitle.Bounds.Height);
            Assert.True(downloaderOrigin.Y > connectionOrigin.Y + connectionTitle.Bounds.Height);
            Assert.True(highSpeedOrigin.Y > downloaderOrigin.Y + downloaderTitle.Bounds.Height);
            Assert.DoesNotContain(
                view.GetVisualDescendants().OfType<TextBlock>(),
                candidate => candidate.Height == 1 && candidate.Background is not null);
            Assert.Equal(
                ResourceColor(application, "BrushTextPrimary", theme),
                SolidColor(connectionTitle.Foreground));
            Assert.Equal(
                ResourceColor(application, "BrushTextPrimary", theme),
                SolidColor(downloaderTitle.Foreground));
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertAriaNetworkHierarchy(
        Avalonia.Application application,
        ThemeVariant theme)
    {
        var view = CreateVisibleAriaSettings();
        var parametersTitle = Assert.IsType<TextBlock>(
            view.FindControl<TextBlock>("NameAriaParametersTitle"));
        var speedSection = Assert.IsType<Border>(
            view.FindControl<Border>("NameAriaSpeedLimitSection"));
        var speedContent = Assert.IsType<StackPanel>(speedSection.Child);
        var speedHeader = Assert.IsType<Grid>(speedContent.Children[0]);
        var speedTitle = Assert.IsType<TextBlock>(
            view.FindControl<TextBlock>("NameAriaSpeedLimitTitle"));
        var speedHint = Assert.IsType<TextBlock>(
            view.FindControl<TextBlock>("NameAriaSpeedLimitHint"));
        var speedRows = speedContent.Children.OfType<Border>().ToArray();
        var overallRow = Assert.IsType<Grid>(speedRows[0].Child);
        var taskRow = Assert.IsType<Grid>(speedRows[1].Child);
        var overallText = Assert.IsType<StackPanel>(overallRow.Children[0]);
        var taskText = Assert.IsType<StackPanel>(taskRow.Children[0]);
        var overallDescription = Assert.IsType<TextBlock>(overallText.Children[1]);
        var taskDescription = Assert.IsType<TextBlock>(taskText.Children[1]);
        var overallLimit = Assert.IsType<TextBox>(
            view.FindControl<TextBox>("NameAriaMaxOverallDownloadLimit"));
        var taskLimit = Assert.IsType<TextBox>(
            view.FindControl<TextBox>("NameAriaMaxDownloadLimit"));
        var window = new Window
        {
            Content = view,
            Width = 760,
            Height = 680
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            var parametersOrigin = Assert.NotNull(parametersTitle.TranslatePoint(default, window));
            var speedOrigin = Assert.NotNull(speedTitle.TranslatePoint(default, window));
            var overallTextOrigin = Assert.NotNull(overallText.TranslatePoint(default, window));
            var taskTextOrigin = Assert.NotNull(taskText.TranslatePoint(default, window));
            var overallOrigin = Assert.NotNull(overallLimit.TranslatePoint(default, window));
            var taskOrigin = Assert.NotNull(taskLimit.TranslatePoint(default, window));

            Assert.Same(speedTitle, speedHeader.Children[0]);
            Assert.True(speedOrigin.Y > parametersOrigin.Y + parametersTitle.Bounds.Height);
            Assert.Equal(new Thickness(0, 1, 0, 1), speedSection.BorderThickness);
            Assert.Null(speedSection.Background);
            Assert.Equal(overallOrigin.X, taskOrigin.X, 0.5);
            Assert.Equal(1, Grid.GetColumn(overallLimit));
            Assert.Equal(1, Grid.GetColumn(taskLimit));
            Assert.True(overallOrigin.X > overallTextOrigin.X + 200);
            Assert.True(taskOrigin.X > taskTextOrigin.X + 200);
            Assert.True(taskOrigin.Y > overallOrigin.Y + overallLimit.Bounds.Height);
            Assert.True(overallLimit.Bounds.Width >= 180);
            Assert.True(taskLimit.Bounds.Width >= 180);
            Assert.Equal(
                ResourceColor(application, "BrushTextPrimary", theme),
                SolidColor(speedTitle.Foreground));
            Assert.Equal(
                ResourceColor(application, "BrushTextSecondary", theme),
                SolidColor(speedHint.Foreground));
            Assert.Equal(
                ResourceColor(application, "BrushTextSecondary", theme),
                SolidColor(overallDescription.Foreground));
            Assert.Equal(
                ResourceColor(application, "BrushTextSecondary", theme),
                SolidColor(taskDescription.Foreground));
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertCustomAriaHierarchy(
        Avalonia.Application application,
        ThemeVariant theme)
    {
        var view = new CustomAriaSettingsView();
        var content = Assert.IsType<StackPanel>(
            view.FindControl<StackPanel>("NameCustomAria"));
        content.IsVisible = true;
        var title = Assert.IsType<TextBlock>(
            view.FindControl<TextBlock>("NameCustomAriaTitle"));
        var requirementTitle = Assert.IsType<TextBlock>(
            view.FindControl<TextBlock>("NameCompatibilityRequirementTitle"));
        var requirementText = Assert.IsType<TextBlock>(
            view.FindControl<TextBlock>("NameCompatibilityRequirementText"));
        var host = Assert.IsType<TextBox>(view.FindControl<TextBox>("NameAriaHost"));
        var port = Assert.IsType<TextBox>(view.FindControl<TextBox>("NameAriaPort"));
        var window = new Window
        {
            Content = view,
            Width = 760,
            Height = 460
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            var titleOrigin = Assert.NotNull(title.TranslatePoint(default, window));
            var requirementOrigin = Assert.NotNull(
                requirementTitle.TranslatePoint(default, window));
            var requirementTextOrigin = Assert.NotNull(
                requirementText.TranslatePoint(default, window));
            var hostOrigin = Assert.NotNull(host.TranslatePoint(default, window));
            var portOrigin = Assert.NotNull(port.TranslatePoint(default, window));

            Assert.True(requirementOrigin.Y > titleOrigin.Y + title.Bounds.Height);
            Assert.True(
                requirementTextOrigin.Y > requirementOrigin.Y + requirementTitle.Bounds.Height);
            Assert.True(hostOrigin.Y > requirementTextOrigin.Y + requirementText.Bounds.Height);
            Assert.Equal(hostOrigin.Y, portOrigin.Y, 0.5);
            Assert.Equal(
                ResourceColor(application, "BrushTextPrimary", theme),
                SolidColor(requirementTitle.Foreground));
            Assert.Equal(
                ResourceColor(application, "BrushTextSecondary", theme),
                SolidColor(requirementText.Foreground));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public Task ExpandedAriaLogLevelUsesProductSelectionBackgroundAcrossThemes()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            var application = DesktopTestResources.EnsureProductThemeResources();
            var originalTheme = application.RequestedThemeVariant;
            var fluentTheme = new FluentTheme();
            application.Styles.Insert(0, fluentTheme);

            try
            {
                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    application.RequestedThemeVariant = theme;
                    var view = CreateVisibleAriaSettings();
                    var comboBox = Assert.IsType<ComboBox>(
                        view.FindControl<ComboBox>("NameAriaLogLevels"));
                    comboBox.ItemsSource = AriaLogLevels;
                    comboBox.SelectedIndex = 1;
                    var window = new Window
                    {
                        Content = view,
                        Width = 840,
                        Height = 620
                    };

                    try
                    {
                        window.Show();
                        window.UpdateLayout();
                        comboBox.IsDropDownOpen = true;
                        window.UpdateLayout();

                        var selected = Assert.IsType<ComboBoxItem>(comboBox.ContainerFromIndex(1));
                        var presenter = Assert.Single(
                            selected.GetVisualDescendants().OfType<ContentPresenter>(),
                            candidate => candidate.Name == "PART_ContentPresenter");

                        Assert.True(comboBox.IsDropDownOpen);
                        Assert.True(selected.IsSelected);
                        Assert.Equal(
                            ResourceColor(application, "BrushSelectionFill", theme),
                            SolidColor(presenter.Background));
                    }
                    finally
                    {
                        window.Close();
                    }
                }
            }
            finally
            {
                application.RequestedThemeVariant = originalTheme;
                application.Styles.Remove(fluentTheme);
            }
        });
    }

    private static AriaDownloaderSettingsView CreateVisibleAriaSettings()
    {
        var view = new AriaDownloaderSettingsView();
        var content = Assert.IsType<StackPanel>(view.FindControl<StackPanel>("NameAria"));
        content.IsVisible = true;
        return view;
    }

    private static void AssertChoiceGrows(
        UserControl view,
        string controlName,
        object itemsSource,
        double baselineWidth)
    {
        var comboBox = Assert.IsType<ComboBox>(view.FindControl<ComboBox>(controlName));
        comboBox.ItemsSource = (System.Collections.IEnumerable)itemsSource;
        comboBox.SelectedIndex = 0;
        var window = new Window
        {
            Content = view,
            Width = 840,
            Height = 620
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            Assert.True(double.IsNaN(comboBox.Width));
            Assert.Equal(baselineWidth, comboBox.MinWidth);
            Assert.True(comboBox.Bounds.Width > baselineWidth);
        }
        finally
        {
            window.Close();
        }
    }

    private static Color ResourceColor(
        Avalonia.Application application,
        string key,
        ThemeVariant theme)
    {
        Assert.True(application.TryGetResource(key, theme, out var value));
        return SolidColor(value);
    }

    private static Color SolidColor(object? value)
    {
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }
}
