# Observability

English | [简体中文](observability.zh-CN.md)

UpperHost provides an enterprise observability baseline without coupling core contracts to a specific backend.

## Stack

- Microsoft.Extensions.Logging remains the logging contract.
- UpperHost.Observability uses Serilog for optional structured rolling-file output.
- .NET ActivitySource and Meter define stable tracing and metrics instruments in UpperHost.Abstractions.
- OpenTelemetry OTLP export is optional and disabled by default.
- IHealthProbe / HealthService remain the health model.

Serilog and OpenTelemetry types do not leak into UpperHost.Abstractions.

## Industrial context

Use these stable fields where they apply: DeviceId, ConnectionId, SessionId, CommandId, Protocol, Transport, Operation, Result, ErrorCode and ElapsedMs.

Never put passwords, tokens, device secrets, signing keys or raw credentials in observability fields.

## Generated application defaults

The generated project enables structured rolling file logging and leaves OTLP disabled.

~~~json
{
  "UpperHost": {
    "Observability": {
      "ServiceName": "UpperHost.App",
      "ServiceVersion": "0.1.0",
      "Logging": {
        "File": {
          "Enabled": true,
          "Path": "logs/upperhost-.json",
          "MinimumLevel": "Information",
          "FileSizeLimitBytes": 52428800,
          "RetainedFileCountLimit": 14
        }
      },
      "Otlp": {
        "Enabled": false,
        "Endpoint": "http://localhost:4317"
      }
    }
  }
}
~~~

File events are JSON, roll daily and by size, and use bounded file retention.

## Metrics

The stable Meter name is UpperHost. Baseline instruments include command executions/failures/duration, transport operations/failures/bytes, reconnect attempts, active alarms, stream frames and dropped frames.

CommandRuntime, starter transports, reconnect behavior and alarms publish into the common instruments.

## Tracing

The stable ActivitySource name is UpperHost. Command execution and observed transport operations create spans. OTLP output is enabled only when explicitly configured.

## Health

TransportHealthProbe summarizes registered transport states: Faulted is Unhealthy, Opening is Degraded, otherwise Healthy. No registered transport is valid because not every scaffold consumer requires a transport.

## Custom backends

Consumer applications can add or replace monitoring backends through standard Microsoft logging and OpenTelemetry extension points. Core UpperHost modules must not depend on a product-specific monitoring backend.
