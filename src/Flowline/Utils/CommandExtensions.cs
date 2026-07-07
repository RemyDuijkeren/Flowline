using CliWrap;
using Flowline.Diagnostics;
using Spectre.Console;

namespace Flowline.Utils;

public static class CommandExtensions
{
    public static Command WithCapture(this Command cmd, SubprocessCapture capture, StatusContext? ctx = null, Func<string, string>? lineTransform = null)
        => capture.Apply(cmd, ctx, lineTransform);
}
