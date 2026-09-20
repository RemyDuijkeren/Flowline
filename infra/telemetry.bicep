@description('Base name for both resources. They are different resource types, so they can share one name.')
param name string

@description('Azure region, passed down from main.bicep.')
param location string

@description('Workspace ingestion cap in GB per day, as a string because Bicep has no decimal type.')
param dailyQuotaGb string

// Pay-as-you-go (PerGB2018) is the only tier open to new workspaces. The legacy Free tier closed to
// new workspaces on 1 July 2022, so staying free means staying under the 5 GB per month allowance
// rather than selecting a free SKU. The daily cap below is what enforces that.
resource workspace 'Microsoft.OperationalInsights/workspaces@2025-07-01' = {
  name: name
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    workspaceCapping: {
      dailyQuotaGb: json(dailyQuotaGb)
    }
    // Left at the default. The Application Insights tables (AppRequests, AppExceptions, AppTraces,
    // AppDependencies and their siblings) carry 90 days of retention at no charge regardless of this
    // value, so raising it buys nothing here and lowering it only loses history.
    retentionInDays: 30
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

// Workspace-based, which is the only creation path now. 'other' is the correct application type for a
// CLI: the web and desktop types change which portal experiences are offered, not what is ingested.
resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: name
  location: location
  kind: 'other'
  properties: {
    Application_Type: 'other'
    WorkspaceResourceId: workspace.id
    IngestionMode: 'LogAnalytics'
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
    DisableIpMasking: false
  }
}

output connectionString string = appInsights.properties.ConnectionString
output workspaceId string = workspace.id
output appInsightsId string = appInsights.id
