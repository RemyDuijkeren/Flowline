using FluentAssertions;
using Flowline.Core.ConnectionReferences;
using Xunit;

namespace Flowline.Core.Tests.ConnectionReferences;

public class FlowConnectionMapTests
{
    static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ConnectionReferences", "Fixtures", name));

    [Fact]
    public void OneEntry_NamesOneReference()
    {
        FlowConnectionMap.ReferencedLogicalNames(Fixture("flow-one-entry.clientdata.json"))
            .Should().BeEquivalentTo(["flsp_spikeB"]);
    }

    // Two entries name flsp_spikeB (different map keys); the flow still counts it once.
    [Fact]
    public void MultiEntry_NamesEachReferenceOnce()
    {
        FlowConnectionMap.ReferencedLogicalNames(Fixture("flow-multi-entry.clientdata.json"))
            .Should().BeEquivalentTo(["flsp_spikeA", "flsp_spikeB", "flsp_sharedwebcontents_a5030"]);
    }

    [Fact]
    public void NamesCompareCaseInsensitively()
    {
        var names = FlowConnectionMap.ReferencedLogicalNames(Fixture("flow-one-entry.clientdata.json"));
        names!.Contains("FLSP_SPIKEB").Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("{\"definition\":{}}")]
    public void EmptyOrUnreadable_IsNull(string? definition)
    {
        FlowConnectionMap.ReferencedLogicalNames(definition).Should().BeNull();
    }

    [Fact]
    public void ReadableFlowWithNoConnections_IsEmptyNotNull()
    {
        FlowConnectionMap.ReferencedLogicalNames("{\"properties\":{\"definition\":{}}}").Should().BeEmpty();
    }
}
