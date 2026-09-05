using System.Diagnostics.CodeAnalysis;

namespace DownKyi.Architecture.Tests;

[CollectionDefinition(Name)]
[SuppressMessage(
    "Maintainability",
    "CA1515:Consider making public types internal",
    Justification = "xUnit requires collection definition classes to be public.")]
public sealed class ResourceFlightRecorderGroup
{
    public const string Name = "Windows resource flight recorder";
}
