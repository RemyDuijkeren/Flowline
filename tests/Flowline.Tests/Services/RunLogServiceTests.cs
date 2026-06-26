using System.Text.Json;
using FluentAssertions;
using Flowline.Services;

namespace Flowline.Tests.Services;

public class RunLogServiceTests : IDisposable
{
    readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    static RunLogRecord MakeRecord(DateTimeOffset? timestamp = null) => new(
        Timestamp: timestamp ?? DateTimeOffset.UtcNow,
        CommandName: "push",
        ArgsRedacted: "MySolution --dev https://example.crm4.dynamics.com",
        ExitCode: 0,
        DurationMs: 1234,
        FlowlineVersion: "1.0.0",
        ToolVersions: new Dictionary<string, string?> { ["dotnet"] = "9.0.0", ["pac"] = "1.30.0", ["git"] = "2.43.0" },
        LogFilePath: "/tmp/logs/2026-06-26.log",
        ExceptionType: null,
        ExceptionMessage: null,
        SubprocessOutput: null
    );

    // 1. AppendToAsync creates the runs directory if it doesn't exist
    [Fact]
    public async Task AppendToAsync_CreatesDirectoryIfMissing()
    {
        var path = Path.Combine(_tempDir, "runs", "2026-06-26.jsonl");
        var service = new RunLogService();

        await service.AppendToAsync(MakeRecord(), path);

        Directory.Exists(Path.GetDirectoryName(path)).Should().BeTrue();
        File.Exists(path).Should().BeTrue();
    }

    // 2. AppendToAsync writes valid JSON; second call appends second line
    [Fact]
    public async Task AppendToAsync_WritesJsonLine_SecondCallAppendsSecondLine()
    {
        Directory.CreateDirectory(_tempDir);
        var path = Path.Combine(_tempDir, "2026-06-26.jsonl");
        var service = new RunLogService();

        await service.AppendToAsync(MakeRecord(), path);
        await service.AppendToAsync(MakeRecord(), path);

        var lines = File.ReadAllLines(path);
        lines.Should().HaveCount(2);
        foreach (var line in lines)
        {
            var act = () => JsonDocument.Parse(line);
            act.Should().NotThrow("each line must be valid JSON");
        }
    }

    // 3. AppendToAsync does not throw when path is unwritable
    [Fact]
    public async Task AppendToAsync_UnwritablePath_DoesNotThrow()
    {
        Directory.CreateDirectory(_tempDir);
        var fileAsDir = Path.Combine(_tempDir, "blocked.txt");
        File.WriteAllText(fileAsDir, "block");
        var invalidPath = Path.Combine(fileAsDir, "sub", "run.jsonl");
        var service = new RunLogService();

        var act = async () => await service.AppendToAsync(MakeRecord(), invalidPath);

        await act.Should().NotThrowAsync();
    }

    // 4. CleanDirectory deletes JSONL files older than 30 days; keeps recent ones
    [Fact]
    public void CleanDirectory_DeletesOldFiles_KeepsRecentFiles()
    {
        Directory.CreateDirectory(_tempDir);
        var today = new DateOnly(2026, 6, 26);
        var oldFile = Path.Combine(_tempDir, "2026-05-01.jsonl");   // 56 days ago
        var edgeFile = Path.Combine(_tempDir, "2026-05-27.jsonl");  // 30 days ago (not > 30)
        var recentFile = Path.Combine(_tempDir, "2026-06-26.jsonl"); // 0 days
        File.WriteAllText(oldFile, "{}");
        File.WriteAllText(edgeFile, "{}");
        File.WriteAllText(recentFile, "{}");

        var samplePath = Path.Combine(_tempDir, "2026-06-26.jsonl");
        RunLogService.CleanDirectory(samplePath, ".jsonl", today);

        File.Exists(oldFile).Should().BeFalse("56-day-old file should be deleted");
        File.Exists(edgeFile).Should().BeTrue("exactly-30-day-old file is not > 30 so kept");
        File.Exists(recentFile).Should().BeTrue("today's file should be kept");
    }

    // 5. CleanOldLogsAsync does not throw when directory doesn't exist
    [Fact]
    public async Task CleanOldLogsAsync_NonexistentDirectory_DoesNotThrow()
    {
        var service = new RunLogService();

        var act = async () => await service.CleanOldLogsAsync(new DateOnly(2099, 1, 1));

        await act.Should().NotThrowAsync();
    }

    // 6. ArgsRedacted field with --client-secret *** serializes correctly
    [Fact]
    public async Task AppendToAsync_RedactedArgs_SerializedAsSnakeCaseField()
    {
        Directory.CreateDirectory(_tempDir);
        var path = Path.Combine(_tempDir, "2026-06-26.jsonl");
        var record = MakeRecord() with { ArgsRedacted = "MySolution --client-secret ***" };
        var service = new RunLogService();

        await service.AppendToAsync(record, path);

        var line = File.ReadAllLines(path)[0];
        line.Should().Contain("\"args_redacted\"");
        line.Should().Contain("--client-secret ***");
        line.Should().Contain("\"exit_code\"");
        line.Should().Contain("\"duration_ms\"");
    }
}
