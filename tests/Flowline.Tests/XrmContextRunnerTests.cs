using FluentAssertions;
using Flowline.Services;

namespace Flowline.Tests;

public class XrmContextRunnerTests
{
    const string SolutionName = "MySolution";
    const string Namespace = "MySolution.Models";
    const string ConnectionString = "AuthType=OAuth;Url=https://org.crm.dynamics.com";
    const string OutputPath = @"C:\temp\output";

    // ── Required args always present ─────────────────────────────────────────

    [Fact]
    public void BuildArgs_ContainsSolutions()
    {
        var args = XrmContextRunner.BuildArgs(SolutionName, null, Namespace, ConnectionString, OutputPath);

        args.Should().Contain($"/solutions:{SolutionName}");
    }

    [Fact]
    public void BuildArgs_ContainsNamespace()
    {
        var args = XrmContextRunner.BuildArgs(SolutionName, null, Namespace, ConnectionString, OutputPath);

        args.Should().Contain($"/namespace:{Namespace}");
    }

    [Fact]
    public void BuildArgs_ContainsConnectionString()
    {
        var args = XrmContextRunner.BuildArgs(SolutionName, null, Namespace, ConnectionString, OutputPath);

        args.Should().Contain($"/connectionString:{ConnectionString}");
    }

    [Fact]
    public void BuildArgs_ContainsOut()
    {
        var args = XrmContextRunner.BuildArgs(SolutionName, null, Namespace, ConnectionString, OutputPath);

        args.Should().Contain($"/out:{OutputPath}");
    }

    // ── /entities conditional ────────────────────────────────────────────────

    [Fact]
    public void BuildArgs_ContainsEntities_WhenExtraTablesHasEntries()
    {
        var extraTables = new[] { "account", "contact" };

        var args = XrmContextRunner.BuildArgs(SolutionName, extraTables, Namespace, ConnectionString, OutputPath);

        args.Should().Contain("/entities:account,contact");
    }

    [Fact]
    public void BuildArgs_OmitsEntities_WhenExtraTablesIsNull()
    {
        var args = XrmContextRunner.BuildArgs(SolutionName, null, Namespace, ConnectionString, OutputPath);

        args.Should().NotContain(a => a.StartsWith("/entities:"));
    }

    [Fact]
    public void BuildArgs_OmitsEntities_WhenExtraTablesIsEmpty()
    {
        var args = XrmContextRunner.BuildArgs(SolutionName, [], Namespace, ConnectionString, OutputPath);

        args.Should().NotContain(a => a.StartsWith("/entities:"));
    }
}
