using System.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace Flowline.Logging;

public sealed class ActivityTraceEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var traceId = Activity.Current?.TraceId.ToString();
        if (traceId is not null)
            logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty("TraceId", traceId));
    }
}
