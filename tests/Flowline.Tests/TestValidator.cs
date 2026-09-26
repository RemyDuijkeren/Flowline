using Flowline.Core.Validation;

namespace Flowline.Tests;

// A throwaway cache file, so nothing touches the user's real validation cache. Unset probes keep their
// "not bound" stubs, so an unmocked tool probe throws instead of reaching the machine's pac or git.
internal static class TestValidator
{
    public static FlowlineValidator Create(ValidationProbes? probes = null) => new(
        new ValidationCacheStore(Path.Combine(Path.GetTempPath(), $"flowline-validation-cache-{Guid.NewGuid()}.json")),
        probes ?? new ValidationProbes());
}
