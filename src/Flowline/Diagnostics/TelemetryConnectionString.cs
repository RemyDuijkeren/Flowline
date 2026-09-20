namespace Flowline.Diagnostics;

// Application Insights connection string, deliberately committed to source: the ingestion key it
// carries is write-only (it can send telemetry, not read or manage the resource), so there is no
// secret to protect. An empty value turns telemetry off before consent is even consulted.
internal static class TelemetryConnectionString
{
    public const string Value = "InstrumentationKey=5c3b6232-258b-4554-87c5-395431470b01;IngestionEndpoint=https://westeurope-5.in.applicationinsights.azure.com/;LiveEndpoint=https://westeurope.livediagnostics.monitor.azure.com/;ApplicationId=a2798a75-fca4-4855-8d96-3213c61812f5";
}
