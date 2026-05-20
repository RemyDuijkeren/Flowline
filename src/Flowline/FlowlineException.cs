using Spectre.Console;

namespace Flowline;

public class FlowlineException(string message) : Exception(message)
{
    public Action<IAnsiConsole>? Detail { get; private set; }

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
