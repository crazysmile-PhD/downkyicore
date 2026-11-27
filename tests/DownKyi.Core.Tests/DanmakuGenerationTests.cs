using System.Collections;
using DownKyi.Core.BiliApi.DanmakuApi.Models;
using DownKyi.Core.Danmaku2Ass;

namespace DownKyi.Core.Tests;

public sealed class DanmakuGenerationTests
{
    [Fact]
    public void LargeAssGenerationDoesNotAllocateRepeatedCopiesOfItsPrefix()
    {
        var input = Enumerable.Range(0, 2000).Select(index => new Danmaku
        {
            Start = index / 4f,
            Style = "scroll",
            Color = 0xFFFFFF,
            Content = "review sample danmaku content",
            SizeRatio = 1
        }).ToArray();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var creator = new Creater(Configuration(), input, TestContext.Current.CancellationToken);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(input.Length, creator.Subtitles.Count);
        Assert.True(creator.Text.Length > 200_000);
        // The former prefix concatenation allocates over 500 MB for this input.
        // Leave ample room for layout objects and runtime variation.
        Assert.True(allocated < 32L * 1024 * 1024);
    }

    [Fact]
    public void CancellationAfterInputMappingStopsAssGeneration()
    {
        using var cancellation = new CancellationTokenSource();
        var input = new CancelingList<BiliDanmaku>(
            [new BiliDanmaku { Mode = 1, Fontsize = 25, Content = "cancel before layout" }],
            cancellation);

        var error = Assert.Throws<OperationCanceledException>(() =>
            new BilibiliDanmakuConverter().CreateAss(Configuration(), input, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public void CancellationDuringLayoutStopsBeforeTheNextDanmaku()
    {
        using var cancellation = new CancellationTokenSource();
        var input = new CancelingList<Danmaku>(
            [Item(), Item()], cancellation, cancelAfter: 1);

        var error = Assert.Throws<OperationCanceledException>(() =>
            new Creater(Configuration(), input, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public void CancellationAfterLayoutStopsBeforeTextGeneration()
    {
        using var cancellation = new CancellationTokenSource();
        var input = new CancelingList<Danmaku>([Item()], cancellation);

        Assert.Throws<OperationCanceledException>(() =>
            new Creater(Configuration(), input, cancellation.Token));
    }

    private static Config Configuration() => new()
    {
        BaseFontSize = 25,
        LineCount = 20,
        LayoutAlgorithm = "sync"
    };

    private static Danmaku Item() => new()
    {
        Style = "top",
        Content = "cancel during generation",
        SizeRatio = 1,
        Color = 0xFFFFFF
    };

    private sealed class CancelingList<T>(T[] items, CancellationTokenSource cancellation, int? cancelAfter = null)
        : IReadOnlyList<T>
    {
        public int Count => items.Length;
        public T this[int index] => items[index];

        public IEnumerator<T> GetEnumerator()
        {
            for (var index = 0; index < items.Length; index++)
            {
                yield return items[index];
                if (index + 1 == (cancelAfter ?? items.Length)) cancellation.Cancel();
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
