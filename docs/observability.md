# Observability

English | [简体中文](observability.zh-CN.md)

OpenDeviceStudio provides an enterprise observability baseline without coupling core contracts to a specific backend.

## Stack

- Microsoft.Extensions.Logging remains the logging contract.
- OpenDeviceStudio.Observability uses Serilog for optional structured rolling-file output.
- .NET ActivitySource and Meter define stable tracing and metrics instruments in OpenDeviceStudio.Abstractions.
- OpenTelemetry OTLP export is optional and disabled by default.
- IHealthProbe / HealthService remain the health model.

Serilog and OpenTelemetry types do not leak into OpenDeviceStudio.Abstractions.

## Industrial context

Use these stable fields where they apply: DeviceId, ConnectionId, SessionId, CommandId, Protocol, Transport, Operation, Result, ErrorCode and ElapsedMs.

Never put passwords, tokens, device secrets, signing keys or raw credentials in observability fields.

## Source scaffold application defaults

The runnable source scaffold application enables structured rolling file logging and leaves OTLP disabled.

~~~json
{
  "OpenDeviceStudio": {
    "Observability": {
      "ServiceName": "OpenDeviceStudio.App",
      "ServiceVersion": "0.1.0",
      "Logging": {
        "File": {
          "Enabled": true,
          "Path": "logs/opendevicestudio-.json",
          "MinimumLevel": "Information",
          "FileSizeLimitBytes": 52428800,
          "RetainedFileCountLimit": 14,
          "AsyncBufferSize": 10000,
          "BlockWhenFull": false
        }
      },
      "Otlp": {
        "Enabled": false,
        "Endpoint": "http://localhost:4317",
        "TraceSampleRatio": 1.0
      }
    }
  }
}
~~~

File events are JSON, roll daily and by size, and use bounded file retention. File I/O is wrapped in a bounded asynchronous buffer so logging cannot grow memory without limit. The default does not block device/control hot paths when the buffer is full; dropped events are surfaced through the `opendevicestudio.logging.async_buffer` health probe.

Known sensitive property names such as passwords, tokens, credentials, authorization values, API keys, private keys and connection strings are redacted before rendering. Applications must still avoid placing secrets in free-form message text.

## Metrics

The stable Meter name is OpenDeviceStudio. Baseline instruments include command executions/failures/duration, transport operations/failures/bytes, reconnect attempts, active alarms, stream frames and dropped frames.

CommandRuntime, starter transports, reconnect behavior and alarms publish into the common instruments.

Metric attributes are intentionally low-cardinality. Per-operation identifiers such as DeviceId, ConnectionId, SessionId and CommandId belong in logs/traces and must not be added to metric attributes. Command duration is recorded in seconds. Active alarm increments/decrements use the same severity attribute set so the series represents a real active count.

## Tracing

The stable ActivitySource name is OpenDeviceStudio. Command execution and observed transport operations create spans. High-cardinality correlation identifiers may be attached to traces. OTLP output is enabled only when explicitly configured, and `TraceSampleRatio` controls parent-based ratio sampling.

## Health

TransportHealthProbe summarizes registered transport states: Faulted is Unhealthy; Opening or Closed is Degraded; all registered transports Open is Healthy. No registered transport is valid because not every scaffold consumer requires a transport.

When async file logging is enabled, `AsyncLogBufferMonitor` exposes queue utilization and dropped-message count through Health. Dropped log events or at least 80% buffer utilization report Degraded.

## Transport registration

Starter transports and custom providers should be registered through `AddOpenDeviceStudioTransport<TTransport>()`. This composition seam first applies ConnectionManager physical-lifecycle ownership, then optional resilience, then the common observed transport pipeline, instead of requiring every Serial/TCP/CAN/BLE/vendor provider to copy lifecycle or observability wrappers independently.

## Custom backends

Consumer applications can add or replace monitoring backends through standard Microsoft logging and OpenTelemetry extension points. Core OpenDeviceStudio modules must not depend on a product-specific monitoring backend.
