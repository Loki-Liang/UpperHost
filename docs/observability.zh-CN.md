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

文件日志采用 JSON Event、按日滚动、按大小滚动，并限制保留文件数量。文件 I/O 外层使用有界异步缓冲，禁止日志积压导致内存无限增长。默认在缓冲满时不阻塞设备控制/采集热路径；丢弃事件通过 `upperhost.logging.async_buffer` Health Probe 暴露。

Password、Token、Credential、Authorization、API Key、Private Key、Connection String 等已知敏感属性在渲染前统一脱敏；业务代码仍禁止把 Secret 直接拼进自由文本日志消息。

## Metrics

稳定 Meter 名称为 UpperHost。基础 Instrument 包括 Command 次数/失败/耗时、Transport 操作/失败/字节数、Reconnect 次数、Active Alarm、Stream Frame 和 Dropped Frame。

CommandRuntime、Starter Transport、Reconnect 和 Alarm 已接入统一 Instrument。

Metric 属性必须保持低基数。DeviceId、ConnectionId、SessionId、CommandId 等逐次变化的关联 ID 只能进入 Log/Trace，禁止进入 Metric Attribute。Command Duration 统一使用秒。Active Alarm 的增减必须使用相同 Severity 属性集合，保证时间序列真实表示当前活动告警数。

## Tracing

稳定 ActivitySource 名称为 UpperHost。Command 执行与 Observed Transport 操作建立 Span；高基数关联 ID 可以进入 Trace。只有显式配置后才通过 OTLP 输出，并由 `TraceSampleRatio` 控制 Parent-Based Ratio Sampling。

## Health

TransportHealthProbe 汇总注册 Transport：Faulted 为 Unhealthy；Opening 或 Closed 为 Degraded；已注册 Transport 全部 Open 才为 Healthy。没有注册 Transport 也是合法状态，因为并非所有脚手架消费者都必须使用 Transport。

启用异步文件日志后，`AsyncLogBufferMonitor` 通过 Health 暴露缓冲占用和 Dropped Message 数；发生丢日志或缓冲占用达到 80% 时报告 Degraded。

## Transport 统一注册入口

Starter Transport 和自定义 Provider 应通过 `AddUpperHostTransport<TTransport>()` 注册。该 Composition Seam 先统一应用 ConnectionManager 物理连接所有权，再叠加可选 Resilience 与 Observed Transport Pipeline，禁止 Serial/TCP/CAN/BLE/厂商 SDK Provider 各自复制生命周期或横切包装逻辑。

## 自定义后端

业务产品可以通过标准 Microsoft Logging / OpenTelemetry 扩展点增加或替换日志、监控后端。UpperHost Core 不得依赖产品专用监控平台。
