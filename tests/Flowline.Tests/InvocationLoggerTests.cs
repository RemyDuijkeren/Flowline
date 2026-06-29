using System.Diagnostics;
using Flowline.Commands;
using Flowline.Core;
using Flowline.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Flowline.Tests;

public class InvocationLoggerTests
{
    static InvocationLoggerTests()
    {
        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
        Activity.ForceDefaultIdFormat = true;
        var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Flowline.CLI",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
    }

    [Fact]
    public void Log_DoesNotThrow_WhenToolVersionsNull()
    {
        var opts = new FlowlineRuntimeOptions();
        var act = () => InvocationLogger.Log(NullLogger.Instance, opts, config: null, rootFolder: "C:\\project", activity: null);
        act.Should().NotThrow();
    }

    [Fact]
    public void Log_SetsActivityTags_WhenToolVersionsPresent()
    {
        var opts = new FlowlineRuntimeOptions
        {
            ToolVersions = new FlowlineToolVersions(
                FlowlineVersion: "1.2.3",
                DotNetVersion: "8.0.100",
                PacVersion: "1.32.0",
                PacInstallType: "dotnet-tool",
                GitVersion: "2.44.0",
                GitBranch: "feat/wave2"),
            IsVerbose = true,
            Force = false
        };

        using var activity = FlowlineActivitySource.Source.StartActivity("test-log");
        activity.Should().NotBeNull("ActivityListener must be registered");

        InvocationLogger.Log(NullLogger.Instance, opts, config: null, rootFolder: "C:\\project", activity);

        activity!.GetTagItem("flowline.version").Should().Be("1.2.3");
        activity.GetTagItem("dotnet.version").Should().Be("8.0.100");
        activity.GetTagItem("pac.version").Should().Be("1.32.0");
        activity.GetTagItem("git.branch").Should().Be("feat/wave2");
        activity.GetTagItem("verbose").Should().Be(true);
        activity.GetTagItem("force").Should().Be(false);
        activity.GetTagItem("project.root").Should().Be("C:\\project");
    }

    [Fact]
    public void Log_DoesNotSetActivityTags_WhenActivityNull()
    {
        var opts = new FlowlineRuntimeOptions
        {
            ToolVersions = new FlowlineToolVersions("1.0.0", "8.0", "1.0", null, "2.44", null)
        };
        var act = () => InvocationLogger.Log(NullLogger.Instance, opts, config: null, rootFolder: "C:\\project", activity: null);
        act.Should().NotThrow();
    }
}
