# Telemetry infrastructure

The Application Insights resource Flowline sends to, defined here rather than clicked together in
the portal. `main.bicep` creates the resource group and calls `telemetry.bicep`, which creates a
workspace-based Application Insights resource and the Log Analytics workspace behind it.

## Deploying

```bash
az deployment sub create \
  --name flowline-telemetry \
  --location westeurope \
  --template-file infra/main.bicep
```

The deployment name is load-bearing: it is how the connection string is read back later.

## Reading the connection string

The connection string is a deployment output, not a value kept in a document:

```bash
az deployment sub show -n flowline-telemetry \
  --query properties.outputs.connectionString.value -o tsv
```

It is embedded in `src/Flowline/Diagnostics/TelemetryConnectionString.cs`. That is deliberate: an
Application Insights ingestion key is write-only, so extracting it from the package buys a spoofer
the ability to send junk, not to read anything. An empty value there turns telemetry off before
consent is consulted, which is what would make a fork's build inert.

## Four things not to change casually

- **The region is permanent.** The ingestion hostname is regional and ships embedded in the
  assembly, so moving the resource breaks every installed client and every customer firewall entry
  naming the old host. Deployed region is West Europe.
- **The daily ingestion cap keeps the resource inside the free allowance.** 0.1 GB per day, roughly
  3.1 GB per month against the 5 GB monthly pay-as-you-go allowance. Reaching it stops ingestion
  until the next UTC day rather than generating cost. Nothing tells you it happened.
- **IP masking stays on.** Azure discards the sender address after a coarse geolocation lookup
  rather than storing it. It is the platform default, and the one identifying value Flowline cannot
  control from the client side.
- **The workspace is what the resource is built on.** A classic, non-workspace resource would change
  how the data is queried and retained.

## Where it currently lives

Subscription `0a2fe3e9-239a-4d15-9884-4a8f8b55a85c`, resource group `flowline`, West Europe.
Ingestion host `westeurope-5.in.applicationinsights.azure.com`.
