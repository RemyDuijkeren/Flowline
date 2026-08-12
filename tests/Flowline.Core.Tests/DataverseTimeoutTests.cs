using FluentAssertions;

namespace Flowline.Core.Tests;

public class DataverseTimeoutTests
{
    // --- Matches ---

    [Fact]
    public void Matches_BareTimeoutException_ReturnsTrue()
    {
        DataverseTimeout.Matches(new TimeoutException("The request channel timed out"), userCancelled: false)
                        .Should().BeTrue();
    }

    [Fact]
    public void Matches_TimeoutNestedInWrapper_ReturnsTrue()
    {
        // The shape the SOAP channel actually produces: an outer TimeoutException wrapping the
        // inner HTTP one, itself surfaced through the SDK's own exception.
        var exception = new InvalidOperationException(
            "Dataverse operation failed",
            new TimeoutException("outer", new TimeoutException("The HTTP request has exceeded the allotted timeout")));

        DataverseTimeout.Matches(exception, userCancelled: false).Should().BeTrue();
    }

    [Fact]
    public void Matches_TimeoutInSecondAggregateChild_ReturnsTrue()
    {
        var exception = new AggregateException(new Exception("unrelated"), new TimeoutException());

        DataverseTimeout.Matches(exception, userCancelled: false).Should().BeTrue();
    }

    [Fact]
    public void Matches_TimeoutException_StillTimeoutWhenUserCancelled()
    {
        // The type is unambiguous — a Ctrl+C racing a timeout doesn't turn one into the other.
        DataverseTimeout.Matches(new TimeoutException(), userCancelled: true).Should().BeTrue();
    }

    [Fact]
    public void Matches_TaskCancelledWithoutUserCancel_ReturnsTrue()
    {
        // HttpClient's timeout shape. Nothing else cancels in Flowline, so no user cancel means
        // the request ran out of time.
        DataverseTimeout.Matches(new TaskCanceledException(), userCancelled: false).Should().BeTrue();
    }

    [Fact]
    public void Matches_TaskCancelledAfterUserCancel_ReturnsFalse()
    {
        DataverseTimeout.Matches(new TaskCanceledException(), userCancelled: true).Should().BeFalse();
    }

    [Fact]
    public void Matches_OperationCancelledAfterUserCancel_ReturnsFalse()
    {
        DataverseTimeout.Matches(new OperationCanceledException(), userCancelled: true).Should().BeFalse();
    }

    [Fact]
    public void Matches_UnrelatedException_ReturnsFalse()
    {
        DataverseTimeout.Matches(new InvalidOperationException("nope"), userCancelled: false).Should().BeFalse();
    }

    [Fact]
    public void Matches_Null_ReturnsFalse()
    {
        DataverseTimeout.Matches(null, userCancelled: false).Should().BeFalse();
    }

    // --- NextStep ---

    [Fact]
    public void NextStep_KnownCommand_NamesTheRerun()
    {
        DataverseTimeout.NextStep("push").Should().Contain("Re-run 'flowline push'");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("--version")]
    public void NextStep_NoUsableCommand_FallsBackToGenericRerun(string? command)
    {
        DataverseTimeout.NextStep(command).Should().Contain("Re-run the command")
                        .And.NotContain("Re-run 'flowline");
    }

    [Fact]
    public void NextStep_DoesNotClaimTheWriteFailed()
    {
        DataverseTimeout.NextStep("push").Should().Contain("It may still have applied the change");
    }
}
