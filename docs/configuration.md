# Configuration contract

[简体中文](configuration.zh-CN.md) | English

OpenDeviceStudio uses `Microsoft.Extensions.Configuration` and `Microsoft.Extensions.Options`. The scaffold owns typed contracts, defaults and validation; applications choose the standard configuration providers appropriate to their deployment.

## Canonical sections

- `OpenDeviceStudio:Transport` -> `OpenDeviceStudioTransportOptions`
- `OpenDeviceStudio:Observability` -> `OpenDeviceStudioObservabilityOptions`

Configured starters register both contracts through the Options pipeline with startup validation. The same validators run during starter composition, so invalid configuration fails before transport/runtime I/O starts.

## Transport

| Path | Default | Constraint |
| --- | --- | --- |
| `OpenDeviceStudio:Transport:Type` | `Simulator` | Simulator, Serial or Tcp |
| `OpenDeviceStudio:Transport:Simulator:Name` | `default` | required for Simulator |
| `OpenDeviceStudio:Transport:Serial:PortName` | none | required for Serial |
| `OpenDeviceStudio:Transport:Serial:BaudRate` | `115200` | > 0 |
| `OpenDeviceStudio:Transport:Serial:DataBits` | `8` | 5..8 |
| `OpenDeviceStudio:Transport:Serial:Parity` | `None` | valid `System.IO.Ports.Parity` |
| `OpenDeviceStudio:Transport:Serial:StopBits` | `One` | valid `System.IO.Ports.StopBits` |
| `OpenDeviceStudio:Transport:Serial:ReadBufferSize` | `16384` | > 0 |
| `OpenDeviceStudio:Transport:Tcp:Host` | none | required for Tcp |
| `OpenDeviceStudio:Transport:Tcp:Port` | `9000` | 1..65535 |
| `OpenDeviceStudio:Transport:Tcp:ReadBufferSize` | `16384` | > 0 |
| `OpenDeviceStudio:Transport:Resilience:Enabled` | `true` | boolean |
| `OpenDeviceStudio:Transport:Resilience:MaxAttempts` | `5` | >= 0 |
| `OpenDeviceStudio:Transport:Resilience:InitialDelayMs` | `250` | >= 0 |
| `OpenDeviceStudio:Transport:Resilience:MaximumDelayMs` | `5000` | >= InitialDelayMs |
| `OpenDeviceStudio:Transport:Resilience:BackoffFactor` | `2.0` | >= 1 |
| `OpenDeviceStudio:Transport:Resilience:ReconnectOnEndOfStream` | `true` | boolean |

## Observability

| Path | Default | Constraint |
| --- | --- | --- |
| `OpenDeviceStudio:Observability:ServiceName` | `OpenDeviceStudio.Application` | non-empty |
| `OpenDeviceStudio:Observability:ServiceVersion` | entry/instrumentation version | non-empty |
| `OpenDeviceStudio:Observability:Logging:File:Enabled` | `false` | boolean |
| `OpenDeviceStudio:Observability:Logging:File:Path` | `logs/opendevicestudio-.json` | valid path when enabled |
| `OpenDeviceStudio:Observability:Logging:File:MinimumLevel` | `Information` | valid `LogLevel` |
| `OpenDeviceStudio:Observability:Logging:File:FileSizeLimitBytes` | `52428800` | > 0 when enabled |
| `OpenDeviceStudio:Observability:Logging:File:RetainedFileCountLimit` | `14` | > 0 when enabled |
| `OpenDeviceStudio:Observability:Logging:File:AsyncBufferSize` | `10000` | > 0 when enabled |
| `OpenDeviceStudio:Observability:Logging:File:BlockWhenFull` | `false` | boolean |
| `OpenDeviceStudio:Observability:Otlp:Enabled` | `false` | boolean |
| `OpenDeviceStudio:Observability:Otlp:Endpoint` | none | absolute HTTP/HTTPS URI when enabled |
| `OpenDeviceStudio:Observability:Otlp:TraceSampleRatio` | `1.0` | > 0 and <= 1 |

Validation failures include the canonical configuration path.

## Secret boundary

OpenDeviceStudio does not implement a credential vault. Applications should inject sensitive values through standard configuration providers such as environment variables, .NET user secrets for local development, or an operating-system/cloud secret store integrated through `Microsoft.Extensions.Configuration`.

Passwords, tokens, API keys, private keys and connection strings must not be committed to the product source scaffold, copied into device metadata, emitted into generated source/test snapshots, or written into free-form log messages. Structured log properties with known sensitive names are redacted before rendering as defense in depth.

Environment variables use the standard double-underscore mapping, for example `OpenDeviceStudio__Transport__Tcp__Host`. Product-specific secret sections belong to the application/provider that consumes them, not to reusable OpenDeviceStudio runtime modules.

## Compatibility

Configuration section names and keys are versioned public surface. Renames/removals require a migration note, synchronized starter-application changes and automated coverage. Defaults live in typed options instead of starter parsing code.
