using FluentAssertions;
using Flowline.Core.Configure;
using Xunit;

namespace Flowline.Core.Tests.Configure;

public class SettingsFileMergerTests
{
    static SettingsDocument Existing(params (string Name, bool Enabled)[] flows)
    {
        var document = new SettingsDocument();
        foreach (var (name, enabled) in flows)
            document.Flows.Add(new ComponentStateEntry(name, enabled));
        return document;
    }

    // R12b: a pull merges rather than regenerating. Re-running `pac solution create-settings` produces a blank
    // skeleton, so a regenerate would wipe every value the team filled in — the well-known annoyance this
    // requirement exists to avoid.
    [Fact]
    public void Merge_ComponentAbsentFromFile_IsAdded()
    {
        var existing = Existing(("Order Processing", false));
        var live = new[] { new ComponentStateEntry("Order Processing", false), new ComponentStateEntry("New Flow", false) };

        var result = SettingsFileMerger.MergeStates(existing.Flows, live);

        result.Merged.Select(e => e.Name).Should().Contain("New Flow");
        result.Added.Should().ContainSingle().Which.Should().Be("New Flow");
    }

    [Fact]
    public void Merge_ExistingEntry_KeepsItsDeclaredValue()
    {
        // The file says this flow must be on; the environment currently has it off. The file wins — that is
        // the whole point of a declaration, and a merge that took the live value would erase the intent.
        var existing = Existing(("Order Processing", true));
        var live = new[] { new ComponentStateEntry("Order Processing", false) };

        var result = SettingsFileMerger.MergeStates(existing.Flows, live);

        result.Merged.Should().ContainSingle().Which.Enabled.Should().BeTrue();
        result.Added.Should().BeEmpty();
    }

    [Fact]
    public void Merge_EntryWhoseComponentVanished_IsReportedNotDropped()
    {
        var existing = Existing(("Order Processing", false), ("Retired Flow", false));
        var live = new[] { new ComponentStateEntry("Order Processing", false) };

        var result = SettingsFileMerger.MergeStates(existing.Flows, live);

        result.Vanished.Should().ContainSingle().Which.Should().Be("Retired Flow");
        result.Merged.Select(e => e.Name).Should().Contain("Retired Flow",
            "a vanished entry is reported for the author to decide on, never silently removed");
    }

    [Fact]
    public void Merge_PreservesExistingOrderAndAppendsNewEntries()
    {
        var existing = Existing(("B flow", false), ("A flow", false));
        var live = new[]
        {
            new ComponentStateEntry("A flow", false),
            new ComponentStateEntry("B flow", false),
            new ComponentStateEntry("C flow", false),
        };

        var result = SettingsFileMerger.MergeStates(existing.Flows, live);

        result.Merged.Select(e => e.Name).Should().ContainInOrder("B flow", "A flow", "C flow");
    }

    [Fact]
    public void Merge_EmptyFile_TakesEveryLiveEntry()
    {
        var result = SettingsFileMerger.MergeStates(new List<ComponentStateEntry>(),
            [new ComponentStateEntry("Order Processing", false)]);

        result.Merged.Should().ContainSingle();
        result.Added.Should().ContainSingle();
        result.Vanished.Should().BeEmpty();
    }

    // A state section lists only the components that are off, so the candidates to add are a subset of what
    // the solution holds. Judging vanished by that subset alone would report every flow someone switched back
    // on as gone, which is why the presence set is a separate argument.
    [Fact]
    public void Merge_DeclaredEntryWhoseComponentIsOn_IsNotReportedAsVanished()
    {
        var existing = Existing(("Order Processing", true));

        var result = SettingsFileMerger.MergeStates(
            existing.Flows,
            live: [],
            presentNames: ["Order Processing"]);

        result.Vanished.Should().BeEmpty();
        result.Merged.Should().ContainSingle().Which.Enabled.Should().BeTrue();
    }

    [Fact]
    public void Merge_DeclaredEntryTheSolutionNoLongerHolds_IsKeptAndReported()
    {
        var existing = Existing(("Deleted flow", false));

        var result = SettingsFileMerger.MergeStates(
            existing.Flows,
            live: [],
            presentNames: ["Something else"]);

        result.Vanished.Should().ContainSingle().Which.Should().Be("Deleted flow");
        result.Merged.Should().ContainSingle("a line someone wrote is reported, never dropped");
    }
}
