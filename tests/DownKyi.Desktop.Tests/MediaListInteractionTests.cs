using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DownKyi.Application.Desktop;
using DownKyi.CustomControl.AsyncImageLoader;
using DownKyi.Images;
using DownKyi.Infrastructure.Settings;
using DownKyi.Platform;
using DownKyi.Presentation;
using DownKyi.ViewModels.UserSpace;
using DownKyi.Views;
using DownKyi.Views.Friends;
using DownKyi.Views.UserSpace;

namespace DownKyi.Desktop.Tests;

public sealed class MediaListInteractionTests
{
    private const string CoverUrl = "https://images.example.test/media-cover.png";
    private static readonly Size[] Sizes = [new(798, 518), new(960, 1000), new(1440, 900)];

    [AvaloniaFact]
    public async Task CoversAndBlankSelectionAreasWorkAcrossAllMediaLists()
    {
        using var fixture = new Fixture();
        using var loader = new CoverLoader();
        var previousBrushLoader = ImageBrushLoader.AsyncImageLoader;
        var previousImageLoader = ImageLoader.AsyncImageLoader;
        ImageBrushLoader.AsyncImageLoader = loader;
        ImageLoader.AsyncImageLoader = loader;
        try
        {
            foreach (var entry in fixture.Entries())
            {
                using var shown = new ShownList(entry);
                var item = shown.Item;
                var cover = Assert.Single(item.GetVisualDescendants().OfType<Control>(), control =>
                    control is Image image && ImageLoader.GetSource(image) == CoverUrl ||
                    control is Border { Background: ImageBrush brush } && ImageBrushLoader.GetSource(brush) == CoverUrl);
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (cover is Image imageControl)
                {
                    imageControl.PropertyChanged += (_, args) =>
                    {
                        if (args.Property == Image.SourceProperty && imageControl.Source != null) { ready.TrySetResult(); }
                    };
                    if (imageControl.Source != null) { ready.TrySetResult(); }
                }
                else
                {
                    var brush = Assert.IsType<ImageBrush>(((Border)cover).Background);
                    brush.PropertyChanged += (_, args) =>
                    {
                        if (args.Property == ImageBrush.SourceProperty && brush.Source != null) { ready.TrySetResult(); }
                    };
                    if (brush.Source != null) { ready.TrySetResult(); }
                }
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true);
                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    fixture.Application.RequestedThemeVariant = theme;
                    foreach (var size in Sizes)
                    {
                        shown.Resize(size);
                        var center = cover.TranslatePoint(new Point(cover.Bounds.Width / 2, cover.Bounds.Height / 2), shown.Window)!.Value;
                        shown.Window.MouseMove(center);
                        shown.Settle();
                        Assert.True(ReferenceEquals(cover, shown.Window.InputHitTest(center)), $"{entry.View.GetType().Name}: hover must leave the cover unobscured.");
                        var gap = entry.Row
                            ? new Point(cover.Bounds.Width + 5, cover.Bounds.Height / 2)
                            : new Point(cover.Bounds.Width - 4, cover.Bounds.Height + 2);
                        var blank = cover.TranslatePoint(gap, shown.Window)!.Value;
                        shown.Window.MouseMove(default);
                        shown.Settle();
                        shown.Window.MouseMove(blank);
                        shown.Settle();
                        Assert.True(item.IsPointerOver, $"{entry.View.GetType().Name}: space between cover and text must be clickable.");
                        Assert.True(fixture.Application.TryGetResource("SpaceHover", theme, out var hover));
                        Assert.Contains(item.GetVisualDescendants(), visual =>
                            (visual is Border border ? border.Background : (visual as Panel)?.Background) is ISolidColorBrush brush &&
                            brush.Color == ((ISolidColorBrush)hover!).Color);
                        shown.Click(blank);
                        Assert.Same(entry.Model, shown.List.SelectedItem);
                        Assert.Empty(fixture.Navigations);
                        shown.List.UnselectAll();
                    }
                }
            }
            Assert.True(loader.RequestCount >= 9);
        }
        finally
        {
            ImageBrushLoader.AsyncImageLoader = previousBrushLoader;
            ImageLoader.AsyncImageLoader = previousImageLoader;
        }
    }

    [AvaloniaFact]
    public void TitleAndCreatorLinksHavePaddingAndActivateOnceWithoutSelectingTheVideo()
    {
        using var fixture = new Fixture();
        foreach (var entry in fixture.Entries().Where(entry => entry.Video))
        {
            using var shown = new ShownList(entry);
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                fixture.Application.RequestedThemeVariant = theme;
                foreach (var size in Sizes)
                {
                    shown.Resize(size);
                    var title = Assert.Single(shown.Item.GetVisualDescendants().OfType<Button>(), button => button.Name == "MediaTitleLink");
                    var links = shown.Item.GetVisualDescendants().OfType<Button>().Where(button => button.Name is "MediaTitleLink" or "MediaCreatorLink").ToArray();
                    Assert.Contains(title, links);
                    foreach (var link in links)
                    {
                        Assert.True(link.Bounds.Height >= 32);
                        var end = link.TranslatePoint(new Point(link.Bounds.Width, link.Bounds.Height), entry.View)!.Value;
                        Assert.InRange(end.X, 0, entry.View.Bounds.Width);
                        Assert.InRange(end.Y, 0, entry.View.Bounds.Height);
                        var paddedPoint = link.TranslatePoint(new Point(2, link.Bounds.Height / 2), shown.Window)!.Value;
                        shown.Window.MouseMove(paddedPoint);
                        shown.Settle();
                        Assert.True(link.IsPointerOver);
                        fixture.Navigations.Clear();
                        shown.Click(paddedPoint, MouseButton.Right);
                        Assert.Empty(fixture.Navigations);
                        shown.List.UnselectAll();
                        shown.Click(paddedPoint);
                        Assert.Single(fixture.Navigations);
                        Assert.Equal(link == title ? AppRoute.VideoDetail : AppRoute.UserSpace, fixture.Navigations[0]);
                        Assert.Null(shown.List.SelectedItem);
                        fixture.Navigations.Clear();
                        Assert.True(link.Focus(NavigationMethod.Tab));
                        shown.Window.KeyPress(Key.Return, RawInputModifiers.None, PhysicalKey.Enter, null);
                        shown.Window.KeyRelease(Key.Return, RawInputModifiers.None, PhysicalKey.Enter, null);
                        Assert.Single(fixture.Navigations);
                        fixture.Navigations.Clear();
                    }
                }
            }
        }
    }

    private sealed record Entry(UserControl View, object Model, string ListName = "NameMedias", bool Row = false, bool Video = true);

    [AvaloniaFact]
    public void FriendRowsNavigateFromWhitespaceAndKeyboardWithoutRightClickNavigation()
    {
        using var fixture = new Fixture();
        using var navigation = new AvaloniaNavigationService(route => { fixture.Navigations.Add(route); return new object(); }, action => action());
        var friend = new FriendInfo(navigation, AppRoute.Friends) { Name = "Creator", Sign = "Profile", Mid = 42 };
        foreach (var view in new UserControl[] { new ViewFollowing(), new ViewFollower() })
        {
            using var shown = new ShownList(new Entry(view, friend, "NameContents", Row: true, Video: false));
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                fixture.Application.RequestedThemeVariant = theme;
                foreach (var size in Sizes)
                {
                    shown.Resize(size);
                    var surface = shown.Item.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "ItemSurface");
                    var gap = surface.TranslatePoint(new Point(surface.Bounds.Width - 24, 50), shown.Window)!.Value;
                    shown.Window.MouseMove(gap);
                    shown.Settle();
                    Assert.True(shown.Item.IsPointerOver);
                    shown.Click(gap, MouseButton.Right);
                    Assert.Empty(fixture.Navigations);
                    shown.Click(gap);
                    Assert.Equal([AppRoute.UserSpace], fixture.Navigations);
                    fixture.Navigations.Clear();
                    var link = Assert.Single(shown.Item.GetVisualDescendants().OfType<Button>());
                    Assert.True(link.Focus(NavigationMethod.Tab));
                    shown.Window.KeyPress(Key.Return, RawInputModifiers.None, PhysicalKey.Enter, null);
                    shown.Window.KeyRelease(Key.Return, RawInputModifiers.None, PhysicalKey.Enter, null);
                    Assert.Equal([AppRoute.UserSpace], fixture.Navigations);
                    fixture.Navigations.Clear();
                }
            }
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly AvaloniaNavigationService _navigation;
        private readonly SettingsStore _settings = new(Path.Combine(Path.GetTempPath(), $"media-interaction-{Guid.NewGuid():N}.json"));
        private readonly FluentTheme _fluent = new();
        private readonly ThemeVariant? _previousTheme;
        public Avalonia.Application Application { get; } = DesktopTestResources.EnsureProductThemeResources();
        public List<AppRoute> Navigations { get; } = [];

        public Fixture()
        {
            DesktopTestResources.EnsureDownloadProjectionResources();
            _previousTheme = Application.RequestedThemeVariant;
            Application.Styles.Insert(0, _fluent);
            _navigation = new AvaloniaNavigationService(route => { Navigations.Add(route); return new object(); }, action => action());
        }

        public IEnumerable<Entry> Entries()
        {
            yield return new(new ViewMyFavorites(), new FavoritesMedia(_navigation, AppRoute.MyFavorites, _settings) { Title = "Video", Bvid = "BV17x411w7KC", Cover = CoverUrl, Duration = "03:12", UpName = "Creator", UpMid = 42 });
            yield return new(new ViewMyToViewVideo(), new ToViewMedia(_navigation, AppRoute.MyToViewVideo, _settings) { Title = "Video", Bvid = "BV17x411w7KC", Cover = CoverUrl, UpName = "Creator", UpMid = 42 }, Row: true);
            yield return new(new ViewMyHistory(), new HistoryMedia(_navigation, AppRoute.MyHistory, _settings) { Title = "Video", Url = "https://www.bilibili.com/video/BV17x411w7KC", Cover = CoverUrl, UpName = "Creator", UpMid = 42, UpAndTagVisibility = true, Platform = NormalIcon.Current.Favorite }, Row: true);
            yield return new(new ViewMyBangumiFollow(), new BangumiFollowMedia(_navigation, AppRoute.MyBangumiFollow) { Title = "Video", MediaId = 1, Cover = CoverUrl }, Row: true);
            yield return new(new ViewPublication(), new PublicationMedia(_navigation, AppRoute.Publication) { Title = "Video", Bvid = "BV17x411w7KC", CoverUrl = CoverUrl, Duration = "03:12" });
            yield return new(new ViewSeasonsSeriesDetail(), new ChannelMedia(_navigation, AppRoute.SeasonsSeries) { Title = "Video", Bvid = "BV17x411w7KC", Cover = CoverUrl, Duration = "03:12" });
            yield return new(new ViewPublicFavorites(), new FavoritesMedia(_navigation, AppRoute.PublicFavorites, _settings) { Title = "Video", Bvid = "BV17x411w7KC", Cover = CoverUrl, Duration = "03:12", UpName = "Creator", UpMid = 42 }, "NameFavoritesMedias", Row: true);
            yield return new(new ViewFavorites(), new FavoriteFolder(1, CoverUrl, NormalIcon.Current.Favorite, "Folder", 1, "2026-10-23"), "NameFavorites", Video: false);
            yield return new(new ViewUserSpaceSeasonsSeries(), new SeasonsSeries { Name = "Series", Cover = CoverUrl, Count = 1, TypeImage = NormalIcon.Current.Favorite }, "NameSeasonsSeries", Video: false);
        }

        public void Dispose()
        {
            Application.RequestedThemeVariant = _previousTheme;
            Application.Styles.Remove(_fluent);
            _settings.Dispose();
            _navigation.Dispose();
        }
    }

    private sealed class ShownList : IDisposable
    {
        public Window Window { get; }
        public ListBox List { get; }
        public ListBoxItem Item => Assert.IsType<ListBoxItem>(List.ContainerFromIndex(0));
        public ShownList(Entry entry)
        {
            List = entry.View.FindControl<ListBox>(entry.ListName)!;
            List.ItemsSource = new[] { entry.Model };
            foreach (var ancestor in List.GetLogicalAncestors().OfType<Control>()) { ancestor.IsVisible = true; }
            Window = new Window { Content = entry.View, Width = 960, Height = 1000 };
            Window.Show();
            Settle();
        }
        public void Resize(Size size) { Window.Width = size.Width; Window.Height = size.Height; Settle(); }
        public void Settle() { Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); Window.UpdateLayout(); }
        public void Click(Point point, MouseButton button = MouseButton.Left) { Window.MouseDown(point, button); Window.MouseUp(point, button); Settle(); }
        public void Dispose() => Window.Close();
    }

    private sealed class CoverLoader : IAsyncImageLoader
    {
        public int RequestCount { get; private set; }
        public Task<Bitmap?> ProvideImageAsync(string url)
        {
            RequestCount++;
            return Task.FromResult<Bitmap?>(new WriteableBitmap(new PixelSize(16, 10), new Vector(96, 96)));
        }
        public void Dispose() { }
    }
}
