using Flowline;
using Flowline.Core;
using FluentAssertions;

namespace Flowline.Tests;

public class BuildBackupLabelTests
{
    [Fact]
    public void BuildBackupLabel_ReturnsDeterministicFormat()
    {
        var utcNow = new DateTime(2026, 7, 3, 21, 45, 30, DateTimeKind.Utc);

        var label = PacUtils.BuildBackupLabel("ContosoCustomizations", utcNow);

        label.Should().Be("flowline-deploy-ContosoCustomizations-20260703T214530Z");
    }

    [Fact]
    public void BuildBackupLabel_PreservesSpacesInSolutionName()
    {
        var utcNow = new DateTime(2026, 7, 3, 21, 45, 30, DateTimeKind.Utc);

        var label = PacUtils.BuildBackupLabel("Contoso Sales", utcNow);

        label.Should().Be("flowline-deploy-Contoso Sales-20260703T214530Z");
    }
}

public class EnsureBackupSucceededTests
{
    [Fact]
    public void EnsureBackupSucceeded_DoesNotThrow_WhenExitCodeIsZero()
    {
        Action act = () => PacUtils.EnsureBackupSucceeded(0, "", "");

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureBackupSucceeded_Throws_WhenExitCodeIsNonZero()
    {
        Action act = () => PacUtils.EnsureBackupSucceeded(1, "", "Error: insufficient privileges");

        act.Should().Throw<FlowlineException>()
            .Where(ex => ex.ExitCode == ExitCode.ConnectionFailed)
            .Where(ex => ex.Message.Contains("insufficient privileges"))
            .Where(ex => ex.Message.Contains("--no-backup"));
    }

    [Fact]
    public void EnsureBackupSucceeded_UsesStandardOutput_WhenStandardErrorIsEmpty()
    {
        Action act = () => PacUtils.EnsureBackupSucceeded(1, "backup failed: quota exceeded", "");

        act.Should().Throw<FlowlineException>()
            .Where(ex => ex.Message.Contains("quota exceeded"));
    }
}
