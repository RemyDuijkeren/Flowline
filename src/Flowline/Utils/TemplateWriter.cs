using Flowline.Core;

namespace Flowline.Utils;

public static class TemplateWriter
{
    public static async Task WriteAsync(string logicalName, string targetPath, CancellationToken cancellationToken = default)
    {
        var stream = typeof(TemplateWriter).Assembly.GetManifestResourceStream(logicalName);

        if (stream is null)
            throw new FlowlineException($"Template '{logicalName}' not found in assembly manifest.");

        await using (stream)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await using var file = File.Create(targetPath);
            await stream.CopyToAsync(file, cancellationToken);
        }
    }
}
