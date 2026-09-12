namespace Flowline.Tests;

/// <summary>
/// Tests that must not run beside each other, because resolving the <c>pac</c> binary goes through
/// process-wide state.
/// </summary>
/// <remarks>
/// <c>PacUtils</c> caches which command it found, and lets a test replace the probe that decides whether
/// a command exists. Both are static, so a class that installs a probe saying "nothing is installed" is
/// answering for every test running at that moment — and xUnit runs classes in parallel by default.
///
/// What that looked like: a test that really invokes <c>pac</c> failing with "Power Platform CLI isn't
/// available", intermittently, and never the same way twice. Sharing a collection makes these classes
/// take turns, which is the narrowest fix that removes the overlap. The wider fix is for the probe and
/// the cache not to be process-wide at all.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class PacResolutionCollection
{
    public const string Name = "pac resolution";
}
