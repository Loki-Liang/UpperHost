# UpperHost 架构

简体中文 | [English](architecture.md)

UpperHost 是**通用设备应用平台**，不是某个行业、某类硬件或某种采集场景的专用框架。

## 稳定平台边界

```text
Presentation (WPF / WinUI / Avalonia / CLI / Service)
        |
Application / Workflow / State Machine / Event Bus
        |
Device Registry + Discovery + Capability Composition
        |
+---------------------+---------------------+
| Command / Parameter |      Streaming      |
| Request / Response  | Dataflow/Backpressure|
+---------------------+---------------------+
        |
Protocol
        |
Transport (Serial / TCP / USB / CAN / BLE / Vendor SDK / ...)
        |
Hardware
```

横切能力包括 Configuration、Hosting/DI、Dataflow、Persistence、Alarm、Diagnostics、Plugin 和 Testing。

## 三条一级应用路线

### Control

用于 PLC、伺服、温控器、电源、实验仪器、执行器等。核心关注命令、参数、状态、ACK、Readback、超时、取消、联锁和故障处理。

### Automation

用于多设备协同、自动工站、测试台和序列控制。核心关注状态机、Workflow、设备协调、告警和故障恢复。

### Acquisition

用于 DAQ、EMG、传感器、相机、遥测和波形。核心关注连续数据流、背压、扇出、存储、算法和显示。

三条路线共享 Device、Protocol、Transport 等稳定边界；任何一条都不应该成为 Core 的行业假设。

## Device 模型

`IDevice` 必须保持小而稳定。功能通过 `IConnectable`、`IConfigurable<T>`、`ICommandable<TCommand,TResult>`、`ICalibratable<,>`、`IParameterProvider`、`IDataSource<T>` 等 Capability 表达。

PLC、相机、运动控制器、实验仪器和生物信号采集设备不应该被强迫进入同一棵继承树。

注册到 DI 的 `IDevice` 会在 Generic Host 启动后进入 `IDeviceRegistry`。`IDeviceDiscoveryService` 聚合各个 `IDeviceDiscoverer` Provider，Core 不需要知道厂商发现协议。

## Command / Request-Response

控制设备通常走 Request/Response：

```text
Domain Command
 -> Guard / State validation
 -> Encoder
 -> Transport.Send
 -> Decoder
 -> Ack / Result
 -> Completion condition / Readback
```

“字节成功发送”不等于“物理动作完成”。具体产品必须定义 ACK、执行完成条件和是否需要 Readback。

Command Guard / Interlock 属于软件控制约束。它们不能代替硬件急停、安全继电器、安全 PLC 或认证安全回路。

## Streaming

连续数据设备走 Streaming：

```text
Transport Receive
 -> Decoder
 -> Typed stream
 -> Dataflow
 -> Storage / Algorithm / Presentation
```

不要让低速命令设备强行通过高频 Streaming 管线，也不要让 UI Timer 成为高频采集驱动器。

## Transport 边界

`ITransport` 只负责打开、关闭、发送、接收字节。Modbus、SCPI、自定义二进制协议或任何领域命令都不应该进入 Transport。

## Protocol 边界

Protocol 负责领域命令和字节之间的转换，拥有 framing 状态，因此必须正确处理碎片输入、粘包、多帧、长度、校验和恢复策略。

## Backpressure

`FanOutHub<T>` 为消费者提供独立 bounded channel。UI 可以使用 drop-oldest；无损存储路径可以使用 wait，并使用独立 hub/pipeline。丢弃策略必须显式，而不是由系统偶然发生。

## State Machine 与 Workflow

- State Machine：描述当前状态以及允许的状态迁移。
- Workflow：描述一项业务/设备任务接下来按什么步骤执行。

典型自动化流程：

```text
connect -> self-test -> configure -> home -> run -> stop
```

每一步调用设备 Capability，而不是直接操作 Socket 或协议缓冲区。

## Events / Alarms

Typed `IEventBus` 用于模块解耦，避免静态全局事件。`IAlarmService` 提供通用 raise/acknowledge/clear 生命周期；产品代码提供报警规则。

## Provider / Plugin

USB、CAN、BLE、数据库、厂商 SDK、行业协议等通过独立 Provider/Starter/Module 接入，禁止把厂商依赖塞进 `UpperHost.Abstractions`。

Plugin 是受信任的进程内扩展，不是安全沙箱。

## Presentation

Core 不依赖 WPF。`UpperHost.Presentation.Wpf` 是适配器。未来 WinUI、Avalonia、CLI、Service 可以复用同一套 Device、Protocol、Workflow 和 Diagnostics。

## Testing

Simulator 是正式开发接缝，不是演示玩具。Fault injection 应支持延迟、丢失、异常等可重复故障，使没有真实硬件时也能验证通信和应用行为。
