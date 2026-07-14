using DownKyi.Domain.Results;

namespace DownKyi.Domain.Tests;

public sealed class OperationResultTests
{
    [Fact]
    public void SuccessfulResultExposesValueWithoutError()
    {
        var result = OperationResult.Success("media-id");

        Assert.True(result.IsSuccess);
        Assert.Null(result.Error);
        Assert.Equal("media-id", result.RequireValue());
    }

    [Fact]
    public void FailedResultPreservesTypedError()
    {
        var error = new OperationError("download.network", "The transfer failed.", OperationErrorKind.Network);

        var result = OperationResult.Failure<string>(error);

        Assert.False(result.IsSuccess);
        Assert.Same(error, result.Error);
        Assert.Throws<InvalidOperationException>(result.RequireValue);
    }
}
