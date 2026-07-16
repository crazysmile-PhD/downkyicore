using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using DownKyi.Application.Lifetime;
using DownKyi.Core.Settings;
using DownKyi.Core.Settings.Models;
using DownKyi.ViewModels;

namespace DownKyi.Views;

internal partial class MainWindow : Window
{
    private readonly IApplicationLifecycle _applicationLifecycle;
    private readonly ISettingsStore _settingsStore;
    private WindowSettings _windowSettings;
    private bool _closeConfirmed;
    private bool _closeInProgress;

    public MainWindow(
        MainWindowViewModel viewModel,
        ISettingsStore settingsStore,
        IApplicationLifecycle applicationLifecycle)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _applicationLifecycle = applicationLifecycle
            ?? throw new ArgumentNullException(nameof(applicationLifecycle));
        InitializeComponent();
        var window = _settingsStore.Current.Window;
        _windowSettings = new WindowSettings
        {
            Width = window.Width,
            Height = window.Height,
            X = window.X,
            Y = window.Y
        };
        ApplyWindowSettings();
        DataContext = viewModel;
    }

    private void ApplyWindowSettings()
    {
        Width = _windowSettings.Width;
        Height = _windowSettings.Height;
        if (double.IsNaN(_windowSettings.X) || double.IsNaN(_windowSettings.Y))
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            Position = new PixelPoint((int)_windowSettings.X, (int)_windowSettings.Y);
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (Design.IsDesignMode || _closeConfirmed)
        {
            SaveWindowSettings();
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        if (_closeInProgress)
        {
            return;
        }

        _closeInProgress = true;
        SaveWindowSettings();
        _ = CompleteCloseAsync();
    }

    private void SaveWindowSettings()
    {
        if (Design.IsDesignMode)
        {
            return;
        }

        if (WindowState == WindowState.Normal)
        {
            _windowSettings.Width = Width;
            _windowSettings.Height = Height;
            _windowSettings.X = Position.X;
            _windowSettings.Y = Position.Y;
        }

        _settingsStore.Update(settings => settings with
        {
            Window = new WindowApplicationSettings(
                _windowSettings.Width,
                _windowSettings.Height,
                _windowSettings.X,
                _windowSettings.Y)
        });
    }

    private async Task CompleteCloseAsync()
    {
        await _applicationLifecycle.RequestShutdownAsync().ConfigureAwait(true);

        _closeConfirmed = true;
        Close();
    }
}
