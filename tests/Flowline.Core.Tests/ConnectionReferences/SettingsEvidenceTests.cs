using FluentAssertions;
using Flowline.Core.ConnectionReferences;
using Flowline.Core.Environments;
using Xunit;

namespace Flowline.Core.Tests.ConnectionReferences;

public sealed class SettingsEvidenceTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("flowline-evidence").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    void Write(string file, string json) => File.WriteAllText(Path.Combine(_dir, file), json);

    [Fact]
    public void ReadsRoleFiles_RoleWithoutFileIsNoEvidence_DevIsNotARole()
    {
        Write("deploymentSettings.prod.json",
            """{"ConnectionReferences":[{"LogicalName":"a_ref","ConnectionId":"CONN-1","ConnectorId":"x"},{"LogicalName":"b_ref","ConnectionId":""}]}""");
        Write("deploymentSettings.test.json", "{}");

        var evidence = SettingsEvidence.Read(_dir);

        evidence.Select(e => e.Role).Should().BeEquivalentTo([EnvironmentRole.Test, EnvironmentRole.Uat, EnvironmentRole.Prod]);
        evidence.Single(e => e.Role == EnvironmentRole.Uat).HasFile.Should().BeFalse();
        evidence.Single(e => e.Role == EnvironmentRole.Test).HasFile.Should().BeTrue();
        var prod = evidence.Single(e => e.Role == EnvironmentRole.Prod);
        prod.ConnectionIds["a_ref"].Should().Be("CONN-1");
        prod.ConnectionIds.ContainsKey("A_REF").Should().BeTrue("lookups are case-insensitive");
        prod.ConnectionIds.ContainsKey("b_ref").Should().BeFalse("an empty id is undeclared");
    }

    [Fact]
    public void SharedFile_GivesNoEvidence()
    {
        Write("deploymentSettings.json", """{"ConnectionReferences":[{"LogicalName":"a_ref","ConnectionId":"C"}]}""");

        SettingsEvidence.Read(_dir).Should().OnlyContain(e => !e.HasFile);
    }

    [Fact]
    public void MalformedRoleFile_RaisesConfigInvalid()
    {
        Write("deploymentSettings.uat.json", "{ broken");

        var act = () => SettingsEvidence.Read(_dir);

        act.Should().Throw<FlowlineException>().Which.ExitCode.Should().Be(ExitCode.ConfigInvalid);
    }
}
