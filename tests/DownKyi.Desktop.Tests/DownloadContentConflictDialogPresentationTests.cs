using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using DownKyi.Application.Desktop;
using DownKyi.Domain.Downloads;
using DownKyi.Platform;
using DownKyi.Services.Download;
using DownKyi.Utils;
using DownKyi.ViewModels.Dialogs;
using DownKyi.Views.Dialogs;

namespace DownKyi.Desktop.Tests;

public sealed class DownloadContentConflictDialogPresentationTests
{
    [AvaloniaFact]
    public Task QualitySubstitutionAndBatchMeaningAreVisibleInExistingDialog()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            DesktopTestResources.EnsureDownloadProjectionResources();
            var viewModel = new DownloadContentConflictDialogViewModel();
            var substitutions = new DownloadQualitySubstitutions(
                new DownloadQualitySubstitution(80, "1080P 高清", 64, "720P 高清"),
                new DownloadQualitySubstitution(30280, "高质量", 30232, "中质量"));
            var prompt = new DownloadContentConflictPrompt(
                "page",
                new DownloadContentConflict(
                    DownloadContentSelection.All,
                    new DownloadMediaCapabilities(DownloadMediaOutputModes.AudioVideo),
                    DownloadContentSelection.All,
                    substitutions));

            viewModel.OnDialogOpened(DownloadContentConflictDialogContract.CreateRequest(prompt));

            Assert.True(viewModel.HasQualitySubstitution);
            Assert.Equal("一鍵確定", viewModel.ApplyToAllText);
            Assert.Equal(
                "畫質：1080P 高清 → 720P 高清；音質：高质量 → 中质量",
                viewModel.QualitySubstitution);
            Assert.Equal(
                "「一鍵確定」表示對本次批次中的每一集，分別使用該集實際可取得、最接近原設定的替代品質。\n\n" +
                "不同集數可能使用不同畫質或音質，不會強制全部使用相同品質，也不會增加原本未勾選的下載內容。\n\n" +
                "使用者仍可選擇跳過下載。",
                DictionaryResource.GetString("ApplyToAllQualitySubstitutionExplanation"));
        });
    }

    [AvaloniaFact]
    public Task QualityBatchShowsGroupedImpactAndExpandableReadableTitles()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            DesktopTestResources.EnsureDownloadProjectionResources();
            var viewModel = new DownloadContentConflictDialogViewModel();
            viewModel.OnDialogOpened(DownloadContentConflictDialogContract.CreateRequest(
                CreateQualityBatchPrompt()));
            var view = new DownloadContentConflictDialog
            {
                DataContext = viewModel
            };
            var window = new DialogWindow
            {
                Content = view
            };

            try
            {
                window.Show();
                var expander = Assert.IsType<Expander>(
                    view.FindControl<Expander>("AffectedQualityItemsExpander"));
                expander.IsExpanded = true;
                window.UpdateLayout();

                Assert.Equal("下載品質需要確認", viewModel.Title);
                Assert.Equal(
                    "本次下載清單中有 3 部影片無法取得偏好品質。確認後會依下列實際品質下載。",
                    viewModel.Message);
                Assert.Equal(
                    ["畫質：1080P 高清 → 720P 高清（2 部）", "畫質：1080P 高清 → 480P 清晰（1 部）"],
                    viewModel.QualitySubstitutionGroups);
                Assert.Equal("查看受影響的 3 部影片", expander.Header);
                Assert.Equal(HorizontalAlignment.Stretch, expander.HorizontalAlignment);
                Assert.Equal(HorizontalAlignment.Stretch, expander.HorizontalContentAlignment);
                Assert.Equal(
                    ["第一批 · 第一集", "第二批 · 第一集", "第三批 · 第一集"],
                    viewModel.QualitySubstitutionItems.Select(item => item.PageName).ToArray());
                var groups = Assert.IsType<ItemsControl>(
                    view.FindControl<ItemsControl>("QualitySubstitutionGroupsList"));
                var affectedItems = Assert.IsType<ItemsControl>(
                    view.FindControl<ItemsControl>("AffectedQualityItemsList"));
                Assert.Same(viewModel.QualitySubstitutionGroups, groups.ItemsSource);
                Assert.Same(viewModel.QualitySubstitutionItems, affectedItems.ItemsSource);
                Assert.NotNull(affectedItems.ItemTemplate);
                Assert.All(
                    view.GetVisualDescendants().OfType<TextBlock>()
                        .Where(text => !string.IsNullOrEmpty(text.Text)),
                    text => Assert.True(text.Bounds.Width <= 720));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [AvaloniaFact]
    public Task IncrementalQualityBatchExplainsThatTheDecisionContinuesAcrossTheCollection()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            DesktopTestResources.EnsureDownloadProjectionResources();
            var viewModel = new DownloadContentConflictDialogViewModel();
            var prompt = DownloadContentConflictPrompt.QualitySubstitutionBatch(
                CreateQualityBatchPrompt().Items,
                appliesToRemainingItems: true);

            viewModel.OnDialogOpened(DownloadContentConflictDialogContract.CreateRequest(prompt));

            Assert.Contains("本次合集後續", viewModel.Message, StringComparison.Ordinal);
            Assert.Contains("不會再次詢問", viewModel.SettingsHint, StringComparison.Ordinal);
            Assert.Equal("依可用品質繼續整個合集", viewModel.UseAvailableContent);
            Assert.Equal("略過合集中的品質替代項目", viewModel.SkipPageText);
        });
    }

    [AvaloniaFact]
    public Task ManyDistinctQualityGroupsUseBoundedSummaryOutsideDecisionActions()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            DesktopTestResources.EnsureDownloadProjectionResources();
            var viewModel = new DownloadContentConflictDialogViewModel();
            viewModel.OnDialogOpened(DownloadContentConflictDialogContract.CreateRequest(
                CreateQualityBatchPromptWithDistinctGroups(24)));
            var view = new DownloadContentConflictDialog
            {
                DataContext = viewModel
            };

            var groupsScroll = Assert.IsType<ScrollViewer>(
                view.FindControl<ScrollViewer>("QualitySubstitutionGroupsScrollViewer"));
            var groups = Assert.IsType<ItemsControl>(
                view.FindControl<ItemsControl>("QualitySubstitutionGroupsList"));
            var useAvailable = Assert.IsType<Button>(
                view.FindControl<Button>("UseAvailableContentButton"));
            var skip = Assert.IsType<Button>(
                view.FindControl<Button>("SkipDownloadPageButton"));

            Assert.Equal(24, viewModel.QualitySubstitutionGroups.Count);
            Assert.Same(viewModel.QualitySubstitutionGroups, groups.ItemsSource);
            Assert.Equal(160, groupsScroll.MaxHeight);
            Assert.Equal(
                ScrollBarVisibility.Disabled,
                groupsScroll.HorizontalScrollBarVisibility);
            Assert.Equal(
                ScrollBarVisibility.Auto,
                groupsScroll.VerticalScrollBarVisibility);
            Assert.Contains(groupsScroll, groups.GetLogicalAncestors());
            Assert.DoesNotContain(groupsScroll, useAvailable.GetLogicalAncestors());
            Assert.DoesNotContain(groupsScroll, skip.GetLogicalAncestors());
        });
    }

    private static DownloadContentConflictPrompt CreateQualityBatchPrompt()
    {
        static DownloadContentConflictPromptItem CreateItem(
            string pageName,
            int selectedQuality,
            string selectedName) => new(
            pageName,
            new DownloadContentConflict(
                DownloadContentSelection.None with { Video = true },
                new DownloadMediaCapabilities(DownloadMediaOutputModes.VideoOnly),
                DownloadContentSelection.None with { Video = true },
                new DownloadQualitySubstitutions(
                    new DownloadQualitySubstitution(
                        80,
                        "1080P 高清",
                        selectedQuality,
                        selectedName),
                    Audio: null)));

        return DownloadContentConflictPrompt.QualitySubstitutionBatch(
        [
            CreateItem("第一批 · 第一集", 64, "720P 高清"),
            CreateItem("第二批 · 第一集", 64, "720P 高清"),
            CreateItem("第三批 · 第一集", 32, "480P 清晰")
        ]);
    }

    private static DownloadContentConflictPrompt CreateQualityBatchPromptWithDistinctGroups(
        int count)
    {
        return DownloadContentConflictPrompt.QualitySubstitutionBatch(
            Enumerable.Range(1, count)
                .Select(index => new DownloadContentConflictPromptItem(
                    $"第 {index} 部影片",
                    new DownloadContentConflict(
                        DownloadContentSelection.None with { Video = true },
                        new DownloadMediaCapabilities(DownloadMediaOutputModes.VideoOnly),
                        DownloadContentSelection.None with { Video = true },
                        new DownloadQualitySubstitutions(
                            new DownloadQualitySubstitution(
                                80,
                                "1080P 高清",
                                index,
                                $"替代畫質 {index}"),
                            Audio: null))))
                .ToArray());
    }

}
