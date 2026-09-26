using Flowline.Core.Validation;
using Flowline.Diagnostics;
using Flowline.Infrastructure;
using Spectre.Console;

namespace Flowline.Tests;

// Both use a throwaway cache file, so nothing touches the user's real validation cache.
internal static class TestValidator
{
    // Stub probes: an unmocked tool probe throws instead of reaching the machine's pac or git.
    public static FlowlineValidator Create() => new(NewStore(), new ValidationProbes());

    // Real pac, git and dotnet probes, for the few tests that assert what the machine has installed.
    public static FlowlineValidator WithRealProbes(IAnsiConsole console) =>
        new(NewStore(), PacValidationProbes.Create(new SubprocessCapture(console)));

    static ValidationCacheStore NewStore() =>
        new(Path.Combine(Path.GetTempPath(), $"flowline-validation-cache-{Guid.NewGuid()}.json"));
}
