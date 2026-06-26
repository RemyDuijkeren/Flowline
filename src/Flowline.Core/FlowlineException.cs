using Spectre.Console;

namespace Flowline;

public class FlowlineException : Exception
{
    public ExitCode ExitCode { get; init; } = ExitCode.GeneralError;
    public Action<IAnsiConsole>? Detail { get; private set; }

    public FlowlineException(string message, Exception? inner = null) : base(message, inner) { }

    public FlowlineException(ExitCode exitCode, string message, Exception? inner = null) : base(message, inner)
    {
        ExitCode = exitCode;
    }

    public FlowlineException WithDetail(Action<IAnsiConsole> detail)
    {
        Detail = detail;
        return this;
    }

    public FlowlineException WithHelpLink(string url)
    {
        HelpLink = url;
        return this;
    }
}
