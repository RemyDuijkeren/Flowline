using System.IO.Compression;
using System.Xml.Linq;
using FluentAssertions;
using Flowline.Commands;
using Flowline.Core;

namespace Flowline.Tests;

public class DeployCommandSolutionManifestTests
{
    // ── ParseSolutionManifest ──────────────────────────────────────────────────

    [Fact]
    public void ParseSolutionManifest_ReturnsVersionAndManagedTrue_WhenManagedIsOne()
    {
        var doc = SolutionXml(version: "1.0.0.1", managed: "1");

        var result = DeployCommand.ParseSolutionManifest(doc);

        result.Version.Should().Be("1.0.0.1");
        result.Managed.Should().BeTrue();
    }

    [Fact]
    public void ParseSolutionManifest_ReturnsManagedFalse_WhenManagedIsZero()
    {
        var doc = SolutionXml(version: "1.0.0.1", managed: "0");

        var result = DeployCommand.ParseSolutionManifest(doc);

        result.Managed.Should().BeFalse();
    }

    [Fact]
    public void ParseSolutionManifest_Throws_WhenVersionMissing()
    {
        var doc = XDocument.Parse("""
            <?xml version="1.0" encoding="utf-8"?>
            <ImportExportXml>
              <SolutionManifest>
              </SolutionManifest>
            </ImportExportXml>
            """);

        var act = () => DeployCommand.ParseSolutionManifest(doc);

        act.Should().Throw<FlowlineException>()
            .Which.ExitCode.Should().Be(ExitCode.ValidationFailed);
    }

    [Fact]
    public void ParseSolutionManifest_ReturnsUniqueName_WhenPresent()
    {
        var doc = SolutionXml(version: "1.0.0.1", managed: "0", uniqueName: "contoso_solution");

        var result = DeployCommand.ParseSolutionManifest(doc);

        result.UniqueName.Should().Be("contoso_solution");
    }

    // KTD3: a missing UniqueName is project-mode-legal (ParseSolutionManifest is shared with
    // ReadLocalSolutionVersion and the history walk) — this must never throw here. The fatal check
    // for standalone mode belongs at a later call site, not in the shared parser.
    [Fact]
    public void ParseSolutionManifest_ReturnsNullUniqueName_WhenElementMissing_AndDoesNotThrow()
    {
        var doc = SolutionXml(version: "1.0.0.1", managed: "0");

        Func<(string Version, bool Managed, string? UniqueName)> act = () => DeployCommand.ParseSolutionManifest(doc);

        act.Should().NotThrow();
        var result = act();
        result.UniqueName.Should().BeNull();
        result.Version.Should().Be("1.0.0.1");
        result.Managed.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseSolutionManifest_ReturnsNullUniqueName_WhenElementIsBlank(string blankValue)
    {
        var doc = SolutionXml(version: "1.0.0.1", managed: "0", uniqueName: blankValue);

        var result = DeployCommand.ParseSolutionManifest(doc);

        result.UniqueName.Should().BeNull();
    }

    // ── ReadLocalSolutionVersion ────────────────────────────────────────────────

    [Fact]
    public void ReadLocalSolutionVersion_Throws_WhenSolutionXmlIsMalformed()
    {
        var dataverseSolutionFolderPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var otherFolder = Path.Combine(dataverseSolutionFolderPath, "src", "Other");
        Directory.CreateDirectory(otherFolder);
        File.WriteAllText(Path.Combine(otherFolder, "Solution.xml"), "<not><valid</xml");

        try
        {
            var act = () => DeployCommand.ReadLocalSolutionVersion(dataverseSolutionFolderPath);

            act.Should().Throw<FlowlineException>()
                .Which.ExitCode.Should().Be(ExitCode.ConfigInvalid);
        }
        finally
        {
            Directory.Delete(dataverseSolutionFolderPath, recursive: true);
        }
    }

    // ── ReadArtifactSolutionManifest ───────────────────────────────────────────

    [Fact]
    public void ReadArtifactSolutionManifest_Throws_WhenZipFileDoesNotExist()
    {
        var zipPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "missing.zip");

        var act = () => DeployCommand.ReadArtifactSolutionManifest(zipPath);

        act.Should().Throw<FlowlineException>()
            .Which.ExitCode.Should().Be(ExitCode.NotFound);
    }

    [Fact]
    public void ReadArtifactSolutionManifest_Throws_WhenSolutionXmlEntryMissing()
    {
        using var tmp = new TempArtifactZip(zip =>
        {
            var entry = zip.CreateEntry("Other/OtherFile.xml");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("<Empty/>");
        });

        var act = () => DeployCommand.ReadArtifactSolutionManifest(tmp.ZipPath);

        act.Should().Throw<FlowlineException>()
            .Which.ExitCode.Should().Be(ExitCode.NotFound);
    }

    [Fact]
    public void ReadArtifactSolutionManifest_Throws_WhenSolutionXmlEntryContentIsMalformed()
    {
        using var tmp = new TempArtifactZip(zip =>
        {
            var entry = zip.CreateEntry("Other/Solution.xml");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("<not><valid</xml");
        });

        var act = () => DeployCommand.ReadArtifactSolutionManifest(tmp.ZipPath);

        act.Should().Throw<FlowlineException>()
            .Which.ExitCode.Should().Be(ExitCode.ValidationFailed);
    }

    [Fact]
    public void ReadArtifactSolutionManifest_Throws_WhenFileIsNotValidZip()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        var zipPath = Path.Combine(dir, "notazip.zip");
        File.WriteAllText(zipPath, "this is definitely not a zip file");

        try
        {
            var act = () => DeployCommand.ReadArtifactSolutionManifest(zipPath);

            act.Should().Throw<FlowlineException>()
                .Which.ExitCode.Should().Be(ExitCode.ValidationFailed);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReadArtifactSolutionManifest_ReturnsVersionAndManaged_WhenZipIsValid()
    {
        using var tmp = new TempArtifactZip(zip =>
        {
            var entry = zip.CreateEntry("Other/Solution.xml");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("""
                <?xml version="1.0" encoding="utf-8"?>
                <ImportExportXml>
                  <SolutionManifest>
                    <Version>2.3.4.5</Version>
                    <Managed>1</Managed>
                  </SolutionManifest>
                </ImportExportXml>
                """);
        });

        var result = DeployCommand.ReadArtifactSolutionManifest(tmp.ZipPath);

        result.Version.Should().Be("2.3.4.5");
        result.Managed.Should().BeTrue();
    }

    // Regression: `pac solution pack` puts solution.xml at the zip root — `Other/Solution.xml` is the
    // *unpacked source* layout. Looking only in Other/ rejected every real packed artifact, so
    // `deploy --path <zip>` could never read a manifest it was actually handed.
    [Fact]
    public void ReadArtifactSolutionManifest_ReadsPackedLayout_SolutionXmlAtZipRoot()
    {
        using var tmp = new TempArtifactZip(zip => WriteManifest(zip, "solution.xml", "3.1.4.1", managed: "0"));

        var result = DeployCommand.ReadArtifactSolutionManifest(tmp.ZipPath);

        result.Version.Should().Be("3.1.4.1");
        result.Managed.Should().BeFalse();
    }

    [Fact]
    public void ReadArtifactSolutionManifest_ReadsPackedLayout_RegardlessOfEntryCasing()
    {
        using var tmp = new TempArtifactZip(zip => WriteManifest(zip, "Solution.xml", "5.0.0.0", managed: "1"));

        var result = DeployCommand.ReadArtifactSolutionManifest(tmp.ZipPath);

        result.Version.Should().Be("5.0.0.0");
        result.Managed.Should().BeTrue();
    }

    // A packed zip carries both a root solution.xml and other entries; the root one wins.
    [Fact]
    public void ReadArtifactSolutionManifest_PrefersRootSolutionXml_WhenBothLayoutsPresent()
    {
        using var tmp = new TempArtifactZip(zip =>
        {
            WriteManifest(zip, "solution.xml", "9.9.9.9", managed: "0");
            WriteManifest(zip, "Other/Solution.xml", "1.1.1.1", managed: "1");
        });

        var result = DeployCommand.ReadArtifactSolutionManifest(tmp.ZipPath);

        result.Version.Should().Be("9.9.9.9");
    }

    [Fact]
    public void ReadArtifactSolutionManifest_ReturnsUniqueName_WhenPresent()
    {
        using var tmp = new TempArtifactZip(zip => WriteManifest(zip, "solution.xml", "3.1.4.1", managed: "0", uniqueName: "contoso_solution"));

        var result = DeployCommand.ReadArtifactSolutionManifest(tmp.ZipPath);

        result.UniqueName.Should().Be("contoso_solution");
    }

    static void WriteManifest(ZipArchive zip, string entryName, string version, string managed, string? uniqueName = null)
    {
        var entry = zip.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open());
        writer.Write($"""
            <?xml version="1.0" encoding="utf-8"?>
            <ImportExportXml>
              <SolutionManifest>
                <Version>{version}</Version>
                <Managed>{managed}</Managed>
                {(uniqueName != null ? $"<UniqueName>{uniqueName}</UniqueName>" : "")}
              </SolutionManifest>
            </ImportExportXml>
            """);
    }

    private static XDocument SolutionXml(string version, string managed, string? uniqueName = null) =>
        XDocument.Parse($"""
            <?xml version="1.0" encoding="utf-8"?>
            <ImportExportXml>
              <SolutionManifest>
                <Version>{version}</Version>
                <Managed>{managed}</Managed>
                {(uniqueName != null ? $"<UniqueName>{uniqueName}</UniqueName>" : "")}
              </SolutionManifest>
            </ImportExportXml>
            """);

    private sealed class TempArtifactZip : IDisposable
    {
        private readonly string _dir;
        public string ZipPath { get; }

        public TempArtifactZip(Action<ZipArchive> configure)
        {
            _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(_dir);
            ZipPath = Path.Combine(_dir, "artifact.zip");

            using var zip = ZipFile.Open(ZipPath, ZipArchiveMode.Create);
            configure(zip);
        }

        public void Dispose() => Directory.Delete(_dir, recursive: true);
    }
}
