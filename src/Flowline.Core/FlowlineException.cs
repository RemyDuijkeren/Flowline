using System.Collections;
using Spectre.Console;

namespace Flowline.Core;

public class FlowlineException : Exception
{
    public ExitCode ExitCode { get; init; } = ExitCode.GeneralError;

    public FlowlineException(string message, Exception? inner = null) : base(message, inner) { }

    public FlowlineException(ExitCode exitCode, string message, Exception? inner = null) : base(message, inner)
    {
        ExitCode = exitCode;
    }

    public FlowlineException WithData(string key, object? value)
    {
        Data[key] = value;
        return this;
    }

    public FlowlineException WithData(Action<IDictionary> configure)
    {
        // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract - we never want to crash if Data is null
        configure?.Invoke(Data);
        return this;
    }

    public FlowlineException WithHelpLink(string url)
    {
        HelpLink = url;
        return this;
    }
}
