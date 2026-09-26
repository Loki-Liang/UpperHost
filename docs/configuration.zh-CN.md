# 配置契约

[English](configuration.md) | 简体中文

OpenDeviceStudio 统一使用 `Microsoft.Extensions.Configuration` 与 `Microsoft.Extensions.Options`。脚手架负责 Typed Options、默认值和验证边界；应用根据部署环境选择标准 Configuration Provider。

## Canonical Section

- `OpenDeviceStudio:Transport` -> `OpenDeviceStudioTransportOptions`
- `OpenDeviceStudio:Observability` -> `OpenDeviceStudioObservabilityOptions`

Configured Starter 通过标准 Options 管线注册并启用启动期验证；同一个 Validator 也在 Starter 组合阶段执行，因此非法配置会在 Transport/Runtime I/O 启动前失败。

## Transport

| 路径 | 默认值 | 约束 |
| --- | --- | --- |
| `OpenDeviceStudio:Transport:Type` | `Simulator` | Simulator、Serial 或 Tcp |
| `OpenDeviceStudio:Transport:Simulator:Name` | `default` | Simulator 必填 |
| `OpenDeviceStudio:Transport:Serial:PortName` | 无 | Serial 必填 |
| `OpenDeviceStudio:Transport:Serial:BaudRate` | `115200` | > 0 |
| `OpenDeviceStudio:Transport:Serial:DataBits` | `8` | 5..8 |
| `OpenDeviceStudio:Transport:Serial:Parity` | `None` | 合法 `System.IO.Ports.Parity` |
| `OpenDeviceStudio:Transport:Serial:StopBits` | `One` | 合法 `System.IO.Ports.StopBits` |
| `OpenDeviceStudio:Transport:Serial:ReadBufferSize` | `16384` | > 0 |
| `OpenDeviceStudio:Transport:Tcp:Host` | 无 | Tcp 必填 |
| `OpenDeviceStudio:Transport:Tcp:Port` | `9000` | 1..65535 |
| `OpenDeviceStudio:Transport:Tcp:ReadBufferSize` | `16384` | > 0 |
| `OpenDeviceStudio:Transport:Resilience:Enabled` | `true` | boolean |
| `OpenDeviceStudio:Transport:Resilience:MaxAttempts` | `5` | >= 0 |
| `OpenDeviceStudio:Transport:Resilience:InitialDelayMs` | `250` | >= 0 |
| `OpenDeviceStudio:Transport:Resilience:MaximumDelayMs` | `5000` | >= InitialDelayMs |
| `OpenDeviceStudio:Transport:Resilience:BackoffFactor` | `2.0` | >= 1 |
| `OpenDeviceStudio:Transport:Resilience:ReconnectOnEndOfStream` | `true` | boolean |

## Observability

| 路径 | 默认值 | 约束 |
| --- | --- | --- |
| `OpenDeviceStudio:Observability:ServiceName` | `OpenDeviceStudio.Application` | 非空 |
| `OpenDeviceStudio:Observability:ServiceVersion` | Entry/Instrumentation 版本 | 非空 |
| `OpenDeviceStudio:Observability:Logging:File:Enabled` | `false` | boolean |
| `OpenDeviceStudio:Observability:Logging:File:Path` | `logs/opendevicestudio-.json` | 启用时必须是合法路径 |
| `OpenDeviceStudio:Observability:Logging:File:MinimumLevel` | `Information` | 合法 `LogLevel` |
| `OpenDeviceStudio:Observability:Logging:File:FileSizeLimitBytes` | `52428800` | 启用时 > 0 |
| `OpenDeviceStudio:Observability:Logging:File:RetainedFileCountLimit` | `14` | 启用时 > 0 |
| `OpenDeviceStudio:Observability:Logging:File:AsyncBufferSize` | `10000` | 启用时 > 0 |
| `OpenDeviceStudio:Observability:Logging:File:BlockWhenFull` | `false` | boolean |
| `OpenDeviceStudio:Observability:Otlp:Enabled` | `false` | boolean |
| `OpenDeviceStudio:Observability:Otlp:Endpoint` | 无 | 启用时必须为绝对 HTTP/HTTPS URI |
| `OpenDeviceStudio:Observability:Otlp:TraceSampleRatio` | `1.0` | > 0 且 <= 1 |

验证错误必须包含 canonical configuration path。

## Secret Boundary

OpenDeviceStudio 不自研凭据保险箱。应用应通过标准配置来源注入敏感值，例如环境变量、本地开发使用 .NET User Secrets，或通过 `Microsoft.Extensions.Configuration` 接入操作系统/云 Secret Store。

Password、Token、API Key、Private Key、Connection String 等敏感值禁止提交到产品源码脚手架、写入 Device metadata、写入 generated source / 测试快照，或直接拼入自由文本日志。已知敏感属性名的结构化日志字段会在输出前脱敏，但这只是纵深防御。

环境变量沿用 .NET 标准双下划线映射，例如 `OpenDeviceStudio__Transport__Tcp__Host`。产品专属 Secret Section 应由实际消费它的应用/Provider 定义，不进入 OpenDeviceStudio 可复用 Runtime。

## 兼容性

配置 Section/Key 属于版本化公共表面。重命名或删除必须提供 migration note、同步 Starter Application 并补自动化覆盖。默认值统一定义在 Typed Options 中，不再散落在 Starter 字符串解析逻辑里。
