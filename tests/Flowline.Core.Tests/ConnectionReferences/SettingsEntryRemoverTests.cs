using FluentAssertions;
using Flowline.Core.Configure;
using Flowline.Core.ConnectionReferences;
using Xunit;

namespace Flowline.Core.Tests.ConnectionReferences;

public sealed class SettingsEntryRemoverTests : IDisposable
{
    readonly string _folder = Directory.CreateTempSubdirectory("flowline-remover-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    // PAC writes no trailing newline, and Flowline's writer matches that.
    static string File3(string nl) => string.Join(nl,
        "{",
        "  \"EnvironmentVariables\": [],",
        "  \"ConnectionReferences\": [",
        "    {",
        "      \"LogicalName\": \"cr123_sharedcommondataserviceforapps_a1\",",
        "      \"ConnectionId\": \"aaa\",",
        "      \"ConnectorId\": \"/providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps\"",
        "    },",
        "    {",
        "      \"LogicalName\": \"cr123_sharedoffice365_b2\",",
        "      \"ConnectionId\": \"bbb\",",
        "      \"ConnectorId\": \"/providers/Microsoft.PowerApps/apis/shared_office365\"",
        "    },",
        "    {",
        "      \"LogicalName\": \"cr123_sharedoffice365_c3\",",
        "      \"ConnectionId\": \"ccc\",",
        "      \"ConnectorId\": \"/providers/Microsoft.PowerApps/apis/shared_office365\"",
        "    }",
        "  ],",
        "  \"CloudFlows\": {",
        "    \"my_flow\": true",
        "  }",
        "}");

    // The file with the given entries (by position: a1, b2, c3) dropped, written out by hand so the
    // expected bytes never come from the code under test.
    static string Without(string nl, params string[] keep)
    {
        var entries = new Dictionary<string, string>
        {
            ["a1"] = "cr123_sharedcommondataserviceforapps_a1|aaa|shared_commondataserviceforapps",
            ["b2"] = "cr123_sharedoffice365_b2|bbb|shared_office365",
            ["c3"] = "cr123_sharedoffice365_c3|ccc|shared_office365",
        };
        var blocks = keep.Select(k =>
        {
            var (name, id, api) = entries[k].Split('|') is [var n, var i, var c] ? (n, i, c) : default;
            return string.Join(nl,
                "    {",
                $"      \"LogicalName\": \"{name}\",",
                $"      \"ConnectionId\": \"{id}\",",
                $"      \"ConnectorId\": \"/providers/Microsoft.PowerApps/apis/{api}\"",
                "    }");
        });
        return string.Join(nl,
            "{",
            "  \"EnvironmentVariables\": [],",
            "  \"ConnectionReferences\": [",
            string.Join("," + nl, blocks),
            "  ],",
            "  \"CloudFlows\": {",
            "    \"my_flow\": true",
            "  }",
            "}");
    }

    string Put(string name, string content)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    public void RemovesOneEntryAndLeavesEveryOtherByteUnchanged(string nl)
    {
        // Removing the middle entry leaves a comma-correct array: "}," of the first stays, last "}" stays.
        var path = Put("deploymentSettings.test.json", File3(nl));

        var result = SettingsEntryRemover.Remove(_folder, ["CR123_SHAREDOFFICE365_B2"]);

        var expected = File3(nl).Replace(
            $"    {{{nl}      \"LogicalName\": \"cr123_sharedoffice365_b2\",{nl}      \"ConnectionId\": \"bbb\",{nl}" +
            $"      \"ConnectorId\": \"/providers/Microsoft.PowerApps/apis/shared_office365\"{nl}    }},{nl}", "");
        File.ReadAllText(path).Should().Be(expected);
        result.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new SettingsEntryRemoval(path, ["cr123_sharedoffice365_b2"]));
    }

    [Fact]
    public void FileThatDoesNotDeclareTheReferenceIsNotRewritten()
    {
        var path = Put("deploymentSettings.test.json", File3("\r\n"));
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);

        var result = SettingsEntryRemover.Remove(_folder, ["cr123_unknown"]);

        result.Should().BeEmpty();
        File.ReadAllText(path).Should().Be(File3("\r\n"));
        File.GetLastWriteTimeUtc(path).Should().Be(stamp);
    }

    [Fact]
    public void RemovesTwoEntriesFromOneFileInOnePass()
    {
        var path = Put("deploymentSettings.test.json", File3("\n"));

        var result = SettingsEntryRemover.Remove(_folder, ["cr123_sharedcommondataserviceforapps_a1", "cr123_sharedoffice365_c3"]);

        File.ReadAllText(path).Should().Be(Without("\n", "b2"));
        result.Single().Removed.Should().BeEquivalentTo(
            ["cr123_sharedcommondataserviceforapps_a1", "cr123_sharedoffice365_c3"]);
    }

    [Fact]
    public void SharedTemplateLosesTheEntryAndARefreshDoesNotBringItBack()
    {
        var path = Put("deploymentSettings.json", File3("\r\n"));

        SettingsEntryRemover.Remove(_folder, ["cr123_sharedoffice365_b2"]);

        // The skeleton PAC would emit after the reference left the solution: it no longer lists it.
        var skeleton = SettingsFileReader.Parse(Without("\r\n", "a1", "c3"));
        var merged = SettingsTemplateService.Merge(skeleton, SettingsFileReader.Read(path));

        merged.Vanished.Should().BeEmpty();
        SettingsFileReader.Write(merged.Document).Should().NotContain("cr123_sharedoffice365_b2");
    }

    [Fact]
    public void EditsEveryRoleFileAndTheSharedFile()
    {
        var test = Put("deploymentSettings.test.json", File3("\n"));
        var prod = Put("deploymentSettings.prod.json", File3("\r\n"));
        var shared = Put("deploymentSettings.json", File3("\n"));
        var other = Put("unrelated.json", File3("\n"));

        var result = SettingsEntryRemover.Remove(_folder, ["cr123_sharedoffice365_b2"]);

        result.Select(r => r.Path).Should().BeEquivalentTo([test, prod, shared]);
        File.ReadAllText(other).Should().Be(File3("\n"));
    }

    [Fact]
    public void MalformedFileRaisesTheConfigError()
    {
        Put("deploymentSettings.test.json", "{ not json");

        var act = () => SettingsEntryRemover.Remove(_folder, ["cr123_x"]);

        act.Should().Throw<FlowlineException>().Which.ExitCode.Should().Be(ExitCode.ConfigInvalid);
    }
}
