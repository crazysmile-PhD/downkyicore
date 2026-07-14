using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using DownKyi.Core.Settings;
using DownKyi.Core.Settings.Models;
using DownKyi.ViewModels;
using Prism.Navigation.Regions;

namespace DownKyi.Views;

internal partial class MainWindow : Window
{
    private const string ContentRegionName = "ContentRegion";
    private WindowSettings _windowSettings;

    public MainWindow()
    {
        InitializeComponent();
        _windowSettings = SettingsManager.Instance.GetWindowSettings().Clone();
        ApplyWindowSettings();
    }

    public MainWindow(MainWindowViewModel viewModel) : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
    }

    public void AttachLegacyRegion()
    {
        RegionManager.SetRegionName(ContentRegionHost, ContentRegionName);
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
        base.OnClosing(e);
        if (Design.IsDesignMode) return;
        if (WindowState == WindowState.Normal)
        {
            _windowSettings.Width = Width;
            _windowSettings.Height = Height;
            _windowSettings.X = Position.X;
            _windowSettings.Y = Position.Y;
        }

        SettingsManager.Instance.SettingWindowSettings(_windowSettings);

        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Dispatcher.UIThread.Post(() => desktop.Shutdown(), DispatcherPriority.Background);
        }
    }

    // protected override void OnClosed(EventArgs e)
    // {
    //     base.OnClosed(e);
    //
    //     // 获取当前窗口的大小和位置
    //     _windowSettings.Width = Width;
    //     _windowSettings.Height = Height;
    //     _windowSettings.X = Position.X;
    //     _windowSettings.Y = Position.Y;
    //
    //     SettingsManager.Instance.SettingWindowSettings(_windowSettings);
    // }
}
