using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls.Documents;
using DownKyi.Application.Desktop;
using DownKyi.Commands;
using DownKyi.Core.Settings;
using DownKyi.Models;
using Microsoft.Extensions.Logging;
using CommunityToolkit.Mvvm.Input;

namespace DownKyi.ViewModels.Dialogs
{
    internal class NewVersionAvailableDialogViewModel : BaseDialogViewModel
    {
        public const string Tag = "NewVersionAvailable";

        private readonly ILogger<NewVersionAvailableDialogViewModel> _logger;
        private readonly IPlatformLauncher _platformLauncher;
        private readonly ISettingsStore _settingsStore;
        private DownKyiAsyncDelegateCommand? _allowCommand;

        private RelayCommand? _skipCurrentVersionCommand;

        public RelayCommand SkipCurrentVersionCommand => _skipCurrentVersionCommand ??= new RelayCommand(ExecuteSkipCurrentVersionCommand);
        public DownKyiAsyncDelegateCommand AllowCommand => _allowCommand ??= new DownKyiAsyncDelegateCommand(ExecuteAllowCommand, _logger);

        public NewVersionAvailableDialogViewModel(
            ISettingsStore settingsStore,
            IPlatformLauncher platformLauncher,
            ILogger<NewVersionAvailableDialogViewModel> logger)
        {
            _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
            _platformLauncher = platformLauncher ?? throw new ArgumentNullException(nameof(platformLauncher));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        private async Task ExecuteAllowCommand()
        {
            var releaseUri = new Uri(
                $"https://github.com/{AppConstant.RepoOwner}/{AppConstant.RepoName}/releases/tag/{TagName}");
            _ = await _platformLauncher.OpenUriAsync(releaseUri).ConfigureAwait(true);
            CloseDialog(AppDialogOutcome.Accepted);
        }

        private void ExecuteSkipCurrentVersionCommand()
        {
            _settingsStore.Update(settings => settings with
            {
                About = settings.About with { SkipVersionOnLaunch = NewVersion }
            });
            CloseDialog(AppDialogOutcome.Canceled);
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

        public override void OnDialogOpened(AppDialogRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            var release = GetRequiredParameter<GitHubRelease>(request, "release");
            EnableSkipVersionOnLaunch = GetRequiredParameter<bool>(request, "enableSkipVersion");
            MarkdownText = release.Body;
            TagName = release.TagName;
            NewVersion = release.TagName.TrimStart('v');
        }
    }
}
