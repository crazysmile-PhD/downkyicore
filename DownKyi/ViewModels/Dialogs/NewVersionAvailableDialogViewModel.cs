using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls.Documents;
using DownKyi.Application.Desktop;
using DownKyi.Commands;
using DownKyi.Core.Settings;
using DownKyi.Models;
using Prism.Commands;
using Prism.Dialogs;

namespace DownKyi.ViewModels.Dialogs
{
    internal class NewVersionAvailableDialogViewModel : BaseDialogViewModel
    {
        public const string Tag = "NewVersionAvailable";

        private readonly IPlatformLauncher _platformLauncher;
        private readonly ISettingsStore _settingsStore;
        private DownKyiAsyncDelegateCommand? _allowCommand;

        private DelegateCommand? _skipCurrentVersionCommand;

        public DelegateCommand SkipCurrentVersionCommand => _skipCurrentVersionCommand ??= new DelegateCommand(ExecuteSkipCurrentVersionCommand);
        public DownKyiAsyncDelegateCommand AllowCommand => _allowCommand ??= new DownKyiAsyncDelegateCommand(ExecuteAllowCommand);

        public NewVersionAvailableDialogViewModel(
            ISettingsStore settingsStore,
            IPlatformLauncher platformLauncher)
        {
            _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
            _platformLauncher = platformLauncher ?? throw new ArgumentNullException(nameof(platformLauncher));
        }

        private async Task ExecuteAllowCommand()
        {
            const ButtonResult result = ButtonResult.OK;
            var releaseUri = new Uri($"https://github.com/{App.RepoOwner}/{App.RepoName}/releases/tag/{TagName}");
            _ = await _platformLauncher.OpenUriAsync(releaseUri).ConfigureAwait(true);
            CloseDialog(new DialogResult(result));
        }

        private void ExecuteSkipCurrentVersionCommand()
        {
            _settingsStore.Update(settings => settings with
            {
                About = settings.About with { SkipVersionOnLaunch = NewVersion }
            });
            CloseDialog(new DialogResult());
        }

        private string _tagName = string.Empty;

        public string TagName
        {
            get => _tagName;
            set => SetProperty(ref _tagName, value);
        }

        private string _markdownText = string.Empty;

        public string MarkdownText
        {
            get => _markdownText;
            set => SetProperty(ref _markdownText, value);
        }

        private bool _enableSkipVersionOnLaunch;


        private string _newVersion = string.Empty;

        private string NewVersion
        {
            get => _newVersion;
            set => SetProperty(ref _newVersion, value);
        }

        public bool EnableSkipVersionOnLaunch
        {
            get => _enableSkipVersionOnLaunch;
            set => SetProperty(ref _enableSkipVersionOnLaunch, value);
        }

        public override void OnDialogOpened(IDialogParameters parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);

            var release = parameters.GetValue<GitHubRelease>("release");
            EnableSkipVersionOnLaunch = parameters.GetValue<bool>("enableSkipVersion");
            MarkdownText = release.Body;
            TagName = release.TagName;
            NewVersion = release.TagName.TrimStart('v');
        }
    }
}
