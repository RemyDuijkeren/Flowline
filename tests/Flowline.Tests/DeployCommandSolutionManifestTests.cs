using System.IO.Compression;
using System.Xml.Linq;
using FluentAssertions;
using Flowline.Commands;

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

    private static XDocument SolutionXml(string version, string managed) =>
        XDocument.Parse($"""
            <?xml version="1.0" encoding="utf-8"?>
            <ImportExportXml>
              <SolutionManifest>
                <Version>{version}</Version>
                <Managed>{managed}</Managed>
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
