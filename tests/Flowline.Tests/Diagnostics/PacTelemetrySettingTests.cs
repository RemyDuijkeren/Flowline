using System.Reflection;
using Flowline.Diagnostics;
using FluentAssertions;
using Xunit;

namespace Flowline.Tests.Diagnostics;

public class PacTelemetrySettingTests : IDisposable
{
    readonly string _tempDir = Directory.CreateTempSubdirectory("flowline-pac-telemetry-tests").FullName;

    string PathFor(string fileName) => Path.Combine(_tempDir, fileName);

    [Fact]
    public void ReadTelemetryEnabled_TelemetryEnabledFalse_ReturnsFalse()
    {
        var path = WriteFile("settings.json", """{"settingVersion":"1.0","uniqueId":"abc","telemetryEnabled":false}""");
        new PacTelemetrySetting(path).ReadTelemetryEnabled().Should().BeFalse();
    }

    [Fact]
    public void ReadTelemetryEnabled_TelemetryEnabledTrue_ReturnsNull_NotConsent()
    {
        var path = WriteFile("settings.json", """{"settingVersion":"1.0","uniqueId":"abc","telemetryEnabled":true}""");
        new PacTelemetrySetting(path).ReadTelemetryEnabled().Should().BeNull();
    }

    [Fact]
    public void ReadTelemetryEnabled_AbsentFile_ReturnsNull()
    {
        var path = PathFor("does-not-exist.json");
        new PacTelemetrySetting(path).ReadTelemetryEnabled().Should().BeNull();
    }

    [Fact]
    public void ReadTelemetryEnabled_MalformedJson_ReturnsNull()
    {
        var path = WriteFile("settings.json", "{not valid json");
        new PacTelemetrySetting(path).ReadTelemetryEnabled().Should().BeNull();
    }

    [Fact]
    public void ReadTelemetryEnabled_MissingProperty_ReturnsNull()
    {
        var path = WriteFile("settings.json", """{"settingVersion":"1.0","uniqueId":"abc"}""");
        new PacTelemetrySetting(path).ReadTelemetryEnabled().Should().BeNull();
    }

    [Fact]
    public void ReadTelemetryEnabled_RenamedProperty_ReturnsNull()
    {
        var path = WriteFile("settings.json", """{"settingVersion":"1.0","telemetry_enabled":false}""");
        new PacTelemetrySetting(path).ReadTelemetryEnabled().Should().BeNull();
    }

    [Fact]
    public void ReadTelemetryEnabled_PropertyIsString_ReturnsNull()
    {
        var path = WriteFile("settings.json", """{"telemetryEnabled":"false"}""");
        new PacTelemetrySetting(path).ReadTelemetryEnabled().Should().BeNull();
    }

    [Fact]
    public void ReadTelemetryEnabled_PropertyIsNumber_ReturnsNull()
    {
        var path = WriteFile("settings.json", """{"telemetryEnabled":0}""");
        new PacTelemetrySetting(path).ReadTelemetryEnabled().Should().BeNull();
    }

    [Fact]
    public void ReadTelemetryEnabled_NeverThrows_ForAnyOfTheAboveInputs()
    {
        var paths = new[]
        {
            PathFor("missing.json"),
            WriteFile("malformed.json", "{{{"),
            WriteFile("wrong-type.json", """{"telemetryEnabled":[1,2,3]}"""),
        };

        foreach (var path in paths)
        {
            var act = () => new PacTelemetrySetting(path).ReadTelemetryEnabled();
            act.Should().NotThrow();
        }
    }

    [Fact]
    public void ReadTelemetryEnabled_NeverWrites_FileContentsAndTimestampUnchanged()
    {
        var path = WriteFile("settings.json", """{"settingVersion":"1.0","uniqueId":"abc","telemetryEnabled":true}""");
        var contentsBefore = File.ReadAllText(path);
        var writeTimeBefore = File.GetLastWriteTimeUtc(path);

        new PacTelemetrySetting(path).ReadTelemetryEnabled();

        File.ReadAllText(path).Should().Be(contentsBefore);
        File.GetLastWriteTimeUtc(path).Should().Be(writeTimeBefore);
    }

    [Fact]
    public void PublicSurface_HasNoWayToReachUniqueId()
    {
        var members = typeof(PacTelemetrySetting)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

        members.Should().NotContain(m => m.Name.Contains("unique", StringComparison.OrdinalIgnoreCase));

        var methods = typeof(PacTelemetrySetting).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName);
        methods.Should().OnlyContain(m => m.ReturnType == typeof(bool?));
    }

    string WriteFile(string fileName, string contents)
    {
        var path = PathFor(fileName);
        File.WriteAllText(path, contents);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
