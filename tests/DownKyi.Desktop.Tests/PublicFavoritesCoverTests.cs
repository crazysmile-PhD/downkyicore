using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DownKyi.Application.Desktop;
using DownKyi.CustomControl.AsyncImageLoader;
using DownKyi.Infrastructure.Settings;
using DownKyi.Platform;
using DownKyi.Presentation;
using DownKyi.Views;

namespace DownKyi.Desktop.Tests;

public sealed class PublicFavoritesCoverTests
{
    [AvaloniaFact]
    public async Task RemoteCoverLoadsIntoTheActualFavoritesRow()
    {
        using var fixture = new Fixture();
        using var loader = new CoverLoader();
        var previous = ImageLoader.AsyncImageLoader;
        ImageLoader.AsyncImageLoader = loader;
        try
        {
            fixture.Media.Cover = "https://images.example.test/favorite-cover.png";
            fixture.Show();
            var image = fixture.Cover;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            image.PropertyChanged += (_, args) =>
            {
                if (args.Property == Image.SourceProperty && image.Source != null) { ready.TrySetResult(); }
            };
            if (image.Source != null) { ready.TrySetResult(); }
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true);
            fixture.Settle();
            Assert.Equal(fixture.Media.Cover, loader.RequestedUrl);
            Assert.IsAssignableFrom<Bitmap>(image.Source);
            Assert.True(image.IsEffectivelyVisible);
            Assert.True(image.Bounds.Width > 0 && image.Bounds.Height > 0);
        }
        finally { ImageLoader.AsyncImageLoader = previous; }
    }

    [AvaloniaFact]
    public void HoverAndSelectionLeaveTheCoverVisibleAcrossThemesAndSizes()
    {
        using var fixture = new Fixture();
        fixture.Show();
        using var bitmap = new WriteableBitmap(new PixelSize(16, 10), new Vector(96, 96));
        fixture.Cover.Source = bitmap;
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            fixture.Application.RequestedThemeVariant = theme;
            foreach (var size in new[] { new Size(798, 518), new Size(960, 1000), new Size(1440, 900) })
            {
                fixture.Window.Width = size.Width;
                fixture.Window.Height = size.Height;
                fixture.Settle();
                var image = fixture.Cover;
                var point = image.TranslatePoint(new Point(image.Bounds.Width / 2, image.Bounds.Height / 2), fixture.Window)!.Value;
                fixture.Window.MouseMove(point);
                fixture.Settle();
                Assert.True(fixture.Item.IsPointerOver);
                Assert.Same(image, fixture.Window.InputHitTest(point));
                var duration = Assert.Single(fixture.Item.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == fixture.Media.Duration);
                Assert.True(duration.IsEffectivelyVisible, "Duration should stay visible.");
                var badge = Assert.IsType<Border>(duration.Parent);
                Assert.True(badge.Bounds.Width < image.Bounds.Width, $"Badge width {badge.Bounds.Width}, image width {image.Bounds.Width}");
                Assert.True(badge.Bounds.Height < image.Bounds.Height / 2, $"Badge height {badge.Bounds.Height}, image height {image.Bounds.Height}");
                fixture.Window.MouseDown(point, MouseButton.Left);
                fixture.Window.MouseUp(point, MouseButton.Left);
                fixture.Settle();
                Assert.Same(bitmap, image.Source);
                Assert.Equal(fixture.Item.IsSelected, fixture.Media.IsSelected);
            }
        }
    }

    private sealed class CoverLoader : IAsyncImageLoader
    {
        public string? RequestedUrl { get; private set; }

        public Task<Bitmap?> ProvideImageAsync(string url, CancellationToken cancellationToken = default)
        {
            RequestedUrl = url;
            return Task.FromResult<Bitmap?>(new WriteableBitmap(new PixelSize(16, 10), new Vector(96, 96)));
        }

        public void Dispose() { }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly AvaloniaNavigationService _navigation = new(route => throw new InvalidOperationException($"Unexpected route: {route}"), action => action());
        private readonly SettingsStore _settings = new(Path.Combine(Path.GetTempPath(), $"favorite-cover-{Guid.NewGuid():N}.json"));
        private readonly FluentTheme _fluent = new();
        private readonly ThemeVariant? _previousTheme;
        private readonly ListBox _list;
        public Avalonia.Application Application { get; } = DesktopTestResources.EnsureProductThemeResources();
        public FavoritesMedia Media { get; }
        public Window Window { get; }
        public ListBoxItem Item => Assert.IsType<ListBoxItem>(_list.ContainerFromIndex(0));
        public Image Cover => Assert.Single(Item.GetVisualDescendants().OfType<Image>());

        public Fixture()
        {
            DesktopTestResources.EnsureDownloadProjectionResources();
            _previousTheme = Application.RequestedThemeVariant;
            Application.Styles.Insert(0, _fluent);
            Media = new FavoritesMedia(_navigation, AppRoute.PublicFavorites, _settings) { Title = "Public favorite", Duration = "04:13" };
            var view = new ViewPublicFavorites();
            view.FindControl<Grid>("PublicFavoritesBody")!.IsVisible = true;
            _list = view.FindControl<ListBox>("NameFavoritesMedias")!;
            _list.ItemsSource = new[] { Media };
            Window = new Window { Content = view, Width = 960, Height = 1000 };
        }

        public void Show() { Window.Show(); Settle(); }

        public void Settle() { Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); Window.UpdateLayout(); }

        public void Dispose()
        {
            Window.Close();
            Application.RequestedThemeVariant = _previousTheme;
            Application.Styles.Remove(_fluent);
            _settings.Dispose();
            _navigation.Dispose();
        }
    }
}
