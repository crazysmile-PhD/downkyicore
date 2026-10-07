using System.Diagnostics.CodeAnalysis;
using Xunit.Sdk;
using Xunit.v3;

namespace DownKyi.Desktop.Tests;

[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
    Justification = "xUnit instantiates this discoverer through AvaloniaFactAttribute's XunitTestCaseDiscoverer registration.")]
internal sealed class AvaloniaFactDiscoverer : FactDiscoverer
{
    public AvaloniaFactDiscoverer()
    {
    }

    protected override IXunitTestCase CreateTestCase(
        ITestFrameworkDiscoveryOptions discoveryOptions,
        IXunitTestMethod testMethod,
        IFactAttribute factAttribute)
    {
        return new AvaloniaTestCase((XunitTestCase)base.CreateTestCase(discoveryOptions, testMethod, factAttribute));
    }
}
