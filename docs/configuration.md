# Configuration contract

[简体中文](configuration.zh-CN.md) | English

UpperHost uses `Microsoft.Extensions.Configuration` and `Microsoft.Extensions.Options`. The scaffold owns typed contracts, defaults and validation; applications choose the standard configuration providers appropriate to their deployment.

## Canonical sections

- `UpperHost:Transport` -> `UpperHostTransportOptions`
- `UpperHost:Observability` -> `UpperHostObservabilityOptions`

Configured starters register both contracts through the Options pipeline with startup validation. The same validators run during starter composition, so invalid configuration fails before transport/runtime I/O starts.

## Transport

| Path | Default | Constraint |
| --- | --- | --- |
| `UpperHost:Transport:Type` | `Simulator` | Simulator, Serial or Tcp |
| `UpperHost:Transport:Simulator:Name` | `default` | required for Simulator |
| `UpperHost:Transport:Serial:PortName` | none | required for Serial |
| `UpperHost:Transport:Serial:BaudRate` | `115200` | > 0 |
| `UpperHost:Transport:Serial:DataBits` | `8` | 5..8 |
| `UpperHost:Transport:Serial:Parity` | `None` | valid `System.IO.Ports.Parity` |
| `UpperHost:Transport:Serial:StopBits` | `One` | valid `System.IO.Ports.StopBits` |
| `UpperHost:Transport:Serial:ReadBufferSize` | `16384` | > 0 |
| `UpperHost:Transport:Tcp:Host` | none | required for Tcp |
| `UpperHost:Transport:Tcp:Port` | `9000` | 1..65535 |
| `UpperHost:Transport:Tcp:ReadBufferSize` | `16384` | > 0 |
| `UpperHost:Transport:Resilience:Enabled` | `true` | boolean |
| `UpperHost:Transport:Resilience:MaxAttempts` | `5` | >= 0 |
| `UpperHost:Transport:Resilience:InitialDelayMs` | `250` | >= 0 |
| `UpperHost:Transport:Resilience:MaximumDelayMs` | `5000` | >= InitialDelayMs |
| `UpperHost:Transport:Resilience:BackoffFactor` | `2.0` | >= 1 |
| `UpperHost:Transport:Resilience:ReconnectOnEndOfStream` | `true` | boolean |

## Observability

| Path | Default | Constraint |
| --- | --- | --- |
| `UpperHost:Observability:ServiceName` | `UpperHost.Application` | non-empty |
| `UpperHost:Observability:ServiceVersion` | entry/instrumentation version | non-empty |
| `UpperHost:Observability:Logging:File:Enabled` | `false` | boolean |
| `UpperHost:Observability:Logging:File:Path` | `logs/upperhost-.json` | valid path when enabled |
| `UpperHost:Observability:Logging:File:MinimumLevel` | `Information` | valid `LogLevel` |
| `UpperHost:Observability:Logging:File:FileSizeLimitBytes` | `52428800` | > 0 when enabled |
| `UpperHost:Observability:Logging:File:RetainedFileCountLimit` | `14` | > 0 when enabled |
| `UpperHost:Observability:Logging:File:AsyncBufferSize` | `10000` | > 0 when enabled |
| `UpperHost:Observability:Logging:File:BlockWhenFull` | `false` | boolean |
| `UpperHost:Observability:Otlp:Enabled` | `false` | boolean |
| `UpperHost:Observability:Otlp:Endpoint` | none | absolute HTTP/HTTPS URI when enabled |
| `UpperHost:Observability:Otlp:TraceSampleRatio` | `1.0` | > 0 and <= 1 |

Validation failures include the canonical configuration path.

## Secret boundary

UpperHost does not implement a credential vault. Applications should inject sensitive values through standard configuration providers such as environment variables, .NET user secrets for local development, or an operating-system/cloud secret store integrated through `Microsoft.Extensions.Configuration`.

Passwords, tokens, API keys, private keys and connection strings must not be committed to the product source scaffold, copied into device metadata, emitted into generated source/test snapshots, or written into free-form log messages. Structured log properties with known sensitive names are redacted before rendering as defense in depth.

Environment variables use the standard double-underscore mapping, for example `UpperHost__Transport__Tcp__Host`. Product-specific secret sections belong to the application/provider that consumes them, not to reusable UpperHost runtime modules.

## Compatibility

Configuration section names and keys are versioned public surface. Renames/removals require a migration note, synchronized starter-application changes and automated coverage. Defaults live in typed options instead of starter parsing code.
