# 配置契约

[English](configuration.md) | 简体中文

UpperHost 统一使用 `Microsoft.Extensions.Configuration` 与 `Microsoft.Extensions.Options`。脚手架负责 Typed Options、默认值和验证边界；应用根据部署环境选择标准 Configuration Provider。

## Canonical Section

- `UpperHost:Transport` -> `UpperHostTransportOptions`
- `UpperHost:Observability` -> `UpperHostObservabilityOptions`

Configured Starter 通过标准 Options 管线注册并启用启动期验证；同一个 Validator 也在 Starter 组合阶段执行，因此非法配置会在 Transport/Runtime I/O 启动前失败。

## Transport

| 路径 | 默认值 | 约束 |
| --- | --- | --- |
| `UpperHost:Transport:Type` | `Simulator` | Simulator、Serial 或 Tcp |
| `UpperHost:Transport:Simulator:Name` | `default` | Simulator 必填 |
| `UpperHost:Transport:Serial:PortName` | 无 | Serial 必填 |
| `UpperHost:Transport:Serial:BaudRate` | `115200` | > 0 |
| `UpperHost:Transport:Serial:DataBits` | `8` | 5..8 |
| `UpperHost:Transport:Serial:Parity` | `None` | 合法 `System.IO.Ports.Parity` |
| `UpperHost:Transport:Serial:StopBits` | `One` | 合法 `System.IO.Ports.StopBits` |
| `UpperHost:Transport:Serial:ReadBufferSize` | `16384` | > 0 |
| `UpperHost:Transport:Tcp:Host` | 无 | Tcp 必填 |
| `UpperHost:Transport:Tcp:Port` | `9000` | 1..65535 |
| `UpperHost:Transport:Tcp:ReadBufferSize` | `16384` | > 0 |
| `UpperHost:Transport:Resilience:Enabled` | `true` | boolean |
| `UpperHost:Transport:Resilience:MaxAttempts` | `5` | >= 0 |
| `UpperHost:Transport:Resilience:InitialDelayMs` | `250` | >= 0 |
| `UpperHost:Transport:Resilience:MaximumDelayMs` | `5000` | >= InitialDelayMs |
| `UpperHost:Transport:Resilience:BackoffFactor` | `2.0` | >= 1 |
| `UpperHost:Transport:Resilience:ReconnectOnEndOfStream` | `true` | boolean |

## Observability

| 路径 | 默认值 | 约束 |
| --- | --- | --- |
| `UpperHost:Observability:ServiceName` | `UpperHost.Application` | 非空 |
| `UpperHost:Observability:ServiceVersion` | Entry/Instrumentation 版本 | 非空 |
| `UpperHost:Observability:Logging:File:Enabled` | `false` | boolean |
| `UpperHost:Observability:Logging:File:Path` | `logs/upperhost-.json` | 启用时必须是合法路径 |
| `UpperHost:Observability:Logging:File:MinimumLevel` | `Information` | 合法 `LogLevel` |
| `UpperHost:Observability:Logging:File:FileSizeLimitBytes` | `52428800` | 启用时 > 0 |
| `UpperHost:Observability:Logging:File:RetainedFileCountLimit` | `14` | 启用时 > 0 |
| `UpperHost:Observability:Logging:File:AsyncBufferSize` | `10000` | 启用时 > 0 |
| `UpperHost:Observability:Logging:File:BlockWhenFull` | `false` | boolean |
| `UpperHost:Observability:Otlp:Enabled` | `false` | boolean |
| `UpperHost:Observability:Otlp:Endpoint` | 无 | 启用时必须为绝对 HTTP/HTTPS URI |
| `UpperHost:Observability:Otlp:TraceSampleRatio` | `1.0` | > 0 且 <= 1 |

验证错误必须包含 canonical configuration path。

## Secret Boundary

UpperHost 不自研凭据保险箱。应用应通过标准配置来源注入敏感值，例如环境变量、本地开发使用 .NET User Secrets，或通过 `Microsoft.Extensions.Configuration` 接入操作系统/云 Secret Store。

Password、Token、API Key、Private Key、Connection String 等敏感值禁止提交到生成模板、写入 Device metadata、写入 generated source / 测试快照，或直接拼入自由文本日志。已知敏感属性名的结构化日志字段会在输出前脱敏，但这只是纵深防御。

环境变量沿用 .NET 标准双下划线映射，例如 `UpperHost__Transport__Tcp__Host`。产品专属 Secret Section 应由实际消费它的应用/Provider 定义，不进入 UpperHost 公共模板。

## 兼容性

配置 Section/Key 属于版本化公共表面。重命名或删除必须提供 migration note、同步模板并补自动化覆盖。默认值统一定义在 Typed Options 中，不再散落在 Starter 字符串解析逻辑里。
