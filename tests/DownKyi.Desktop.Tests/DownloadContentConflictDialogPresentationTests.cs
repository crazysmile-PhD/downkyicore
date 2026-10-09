using DownKyi.Application.Desktop;
using DownKyi.Domain.Downloads;
using DownKyi.Services.Download;
using DownKyi.Utils;
using DownKyi.ViewModels.Dialogs;

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
}
