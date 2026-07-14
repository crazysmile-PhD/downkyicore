using DownKyi.ViewModels;

namespace DownKyi.Tests;

public sealed class RangeObservableCollectionTests
{
    [Fact]
    public void ReplaceRangeRejectsNullItems()
    {
        var collection = new RangeObservableCollection<int>();

        Assert.Throws<ArgumentNullException>(() => collection.ReplaceRange(null!));
    }
}
