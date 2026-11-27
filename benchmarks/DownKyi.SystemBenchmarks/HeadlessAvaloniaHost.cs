using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;

namespace DownKyi.SystemBenchmarks;

internal sealed class HeadlessAvaloniaHost : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;

    public HeadlessAvaloniaHost()
    {
        _thread = new Thread(RunDispatcher)
        {
            IsBackground = true,
            Name = "DownKyi system benchmark UI"
        };
        if (OperatingSystem.IsWindows())
        {
            _thread.SetApartmentState(ApartmentState.STA);
        }

        _thread.Start();
    }

    public async Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        await _started.Task.ConfigureAwait(false);
        return await Dispatcher.UIThread.InvokeAsync(action).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        Dispatcher.UIThread.Post(static () => { }, DispatcherPriority.Send);
        var joined = await Task.Run(
            () => _thread.Join(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        _shutdown.Dispose();
        if (!joined)
        {
            throw new TimeoutException("The benchmark UI thread did not stop within five seconds.");
        }
    }

    private void RunDispatcher()
    {
        try
        {
            AppBuilder
                .Configure<BenchmarkApplication>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                .SetupWithoutStarting();
            _started.TrySetResult();
            Dispatcher.UIThread.MainLoop(_shutdown.Token);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or PlatformNotSupportedException
            or TypeInitializationException)
        {
            _started.TrySetException(exception);
        }
    }

    private sealed class BenchmarkApplication : Avalonia.Application
    {
        public override void Initialize()
        {
            AddProductResource("avares://DownKyi.Desktop/Languages/zh-CN.axaml");
            AddProductResource("avares://DownKyi.Desktop/Resources/Bilibili/BilibiliImages.axaml");
        }

        private void AddProductResource(string source)
        {
            Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://DownKyi.Desktop/"))
            {
                Source = new Uri(source)
            });
        }
    }
}
