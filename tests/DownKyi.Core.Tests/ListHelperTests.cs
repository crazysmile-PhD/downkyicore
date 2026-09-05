using System.Collections.ObjectModel;
using System.Reflection;
using DownKyi.Core.Utils;

namespace DownKyi.Core.Tests;

public sealed class ListHelperTests
{
    [Fact]
    public void InsertUniqueMovesExistingItemWithoutDuplicatingIt()
    {
        Collection<string> values = ["first", "selected", "last"];

        ListHelper.InsertUnique(values, "selected", 0);

        Assert.Equal(["selected", "first", "last"], values);
    }

    [Fact]
    public void InsertUniqueInsertsMissingItemAtRequestedIndex()
    {
        Collection<string> values = ["first", "last"];

        ListHelper.InsertUnique(values, "selected", 1);

        Assert.Equal(["first", "selected", "last"], values);
    }

    [Fact]
    public void InsertUniquePublicContractHasNoByRefSelectionParameter()
    {
        var methods = typeof(ListHelper)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => string.Equals(method.Name, nameof(ListHelper.InsertUnique), StringComparison.Ordinal))
            .ToArray();

        var method = Assert.Single(methods);
        Assert.DoesNotContain(method.GetParameters(), parameter => parameter.ParameterType.IsByRef);
    }
}
