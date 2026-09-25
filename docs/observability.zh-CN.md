# 可观测性 Observability

简体中文 | [English](observability.md)

UpperHost 提供企业级 Observability 基线，但 Core Contract 不绑定某一个日志或监控后端。

## 技术栈

- Microsoft.Extensions.Logging 继续作为日志契约。
- UpperHost.Observability 使用 Serilog 提供可选的结构化滚动文件日志。
- UpperHost.Abstractions 只使用 .NET ActivitySource / Meter 定义稳定 Trace/Metrics Instrument。
- OpenTelemetry OTLP Export 可选，默认关闭。
- Health 继续复用 IHealthProbe / HealthService。

Serilog/OpenTelemetry 类型不会进入 UpperHost.Abstractions 公共契约。

## 工业上位机统一上下文

适用时统一使用 DeviceId、ConnectionId、SessionId、CommandId、Protocol、Transport、Operation、Result、ErrorCode、ElapsedMs。

禁止把 Password、Token、设备密钥、签名密钥或原始 Credential 写入这些字段。

## 生成工程默认配置

生成的上位机工程默认启用结构化滚动文件日志，OTLP 默认关闭。

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

文件日志采用 JSON Event、按日滚动、按大小滚动，并限制保留文件数量。

## Metrics

稳定 Meter 名称为 UpperHost。基础 Instrument 包括 Command 次数/失败/耗时、Transport 操作/失败/字节数、Reconnect 次数、Active Alarm、Stream Frame 和 Dropped Frame。

CommandRuntime、Starter Transport、Reconnect 和 Alarm 已接入统一 Instrument。

## Tracing

稳定 ActivitySource 名称为 UpperHost。Command 执行与 Observed Transport 操作建立 Span；只有显式配置后才通过 OTLP 输出。

## Health

TransportHealthProbe 汇总注册 Transport：Faulted 为 Unhealthy，Opening 为 Degraded，其他为 Healthy。没有注册 Transport 也是合法状态，因为并非所有脚手架消费者都必须使用 Transport。

## 自定义后端

业务产品可以通过标准 Microsoft Logging / OpenTelemetry 扩展点增加或替换日志、监控后端。UpperHost Core 不得依赖产品专用监控平台。
