targetScope = 'subscription'

@description('Base name applied to the resource group, the Log Analytics workspace and the Application Insights resource.')
@minLength(4)
@maxLength(63)
param name string = 'flowline'

@description('Azure region. The Application Insights ingestion hostname is regional and ships embedded in the Flowline assembly, so a later change breaks every installed client and every customer firewall allowlist. Treat this as permanent.')
param location string = 'westeurope'

@description('Workspace ingestion cap in GB per day, as a string because Bicep has no decimal type. Reaching it stops ingestion until the next UTC day rather than incurring cost. 0.1 GB/day is about 3.1 GB/month, inside the 5 GB per month pay-as-you-go free allowance.')
param dailyQuotaGb string = '0.1'

resource rg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: name
  location: location
}

module telemetry 'telemetry.bicep' = {
  name: 'telemetry'
  scope: rg
  params: {
    name: name
    location: location
    dailyQuotaGb: dailyQuotaGb
  }
}

@description('Full connection string to embed in TelemetryConnectionString.cs at U6. The instrumentation key it carries is write-only; Microsoft documents it as not being a security token.')
output connectionString string = telemetry.outputs.connectionString

output workspaceId string = telemetry.outputs.workspaceId
output appInsightsId string = telemetry.outputs.appInsightsId
