# OpenDeviceStudio 二次开发基础能力

简体中文 | [English](secondary-development.md)

这份文档回答两个问题：

1. **OpenDeviceStudio 当前已经提供哪些基础能力，不需要产品项目重复建设？**
2. **二次开发时应该从哪里接入、替换或扩展？**

OpenDeviceStudio 的定位是源码直接二开的企业级 .NET 上位机开发脚手架。产品代码优先放在 `app/OpenDeviceStudio.App`；只有确认能够跨产品复用的能力，才下沉到 `src/OpenDeviceStudio.*`。

## 1. 当前内置能力总览

| 能力 | OpenDeviceStudio 当前提供 | 二开入口 |
| --- | --- | --- |
| 应用 Host / 生命周期 | 基于 .NET Generic Host；统一 Start / Stop / Dispose | `OpenDeviceStudioApplication.CreateBuilder()`、`AddHostedService<T>()` |
| DI 依赖注入 | Microsoft.Extensions.DependencyInjection | `builder.Services` |
| Configuration | Generic Host Configuration；支持 appsettings、环境变量、命令行等标准来源 | `builder.Configuration`、Options Pattern |
| Logging | `Microsoft.Extensions.Logging` 作为业务契约；Console；可选 Serilog JSON 滚动文件日志 | 注入 `ILogger<T>`；通过 Observability 配置或标准 Logging Provider 扩展 |
| Metrics / Tracing | .NET `Meter` / `ActivitySource`；可选 OpenTelemetry OTLP | 标准 OpenTelemetry 扩展点 |
| Health | `IHealthProbe` + `HealthService`；Transport、日志缓冲等已有 Probe | 实现并注册新的 `IHealthProbe` |
| Device 模型 | `IDevice` + Capability 组合；自动进入 `IDeviceRegistry` | 实现 `IDevice`、`IConnectable`、`ICommandable<,>` 等 |
| Device Discovery | 多 Provider `IDeviceDiscoverer` 聚合 | 实现并注册 `IDeviceDiscoverer` |
| 连接生命周期 | `IConnectionManager`；Shared / Exclusive Lease | 业务层获取托管连接，不自行争抢物理 Handle |
| Transport | Serial、TCP、Simulator；统一 `ITransport` 扩展边界 | 实现 `ITransport`，通过 `AddOpenDeviceStudioTransport<T>()` 注册 |
| Protocol | Command Encoder、Message Decoder、Request/Response / Streaming 边界 | 实现 `ICommandEncoder<T>` / `IMessageDecoder<T>` |
| Control Runtime | Host-owned bounded Command Dispatcher、Guard / Interlock、UnknownOutcome、资源仲裁、参数/Readback 基础能力 | 每个强类型命令合同通过 `AddCommandDispatcher<TCommand,TResult>()` 注册；产品定义安全和完成语义 |
| Streaming / Dataflow | 有界 Fan-out、背压与丢弃策略基础能力 | 将设备 Stream 接入 Dataflow，明确容量和 loss policy |
| Workflow | `WorkflowRunner` | 产品定义 `WorkflowDefinition` / `IWorkflowStep` |
| State Machine | 通用 `StateMachine<TState,TTrigger>` | 产品定义状态与触发器 |
| Event | Typed `IEventBus` | 发布/订阅产品事件，不使用全局静态事件 |
| Alarm | `IAlarmService` raise / acknowledge / clear 生命周期 | 产品定义报警规则与触发条件 |
| Storage | `IKeyValueStore` + JSON Provider；默认 Canonical RawData `IRawRecorder` + FileSystem Arrow Adapter | `AddFileSystemStorage()`；采集 Raw artifact 通过配置或替换 `IRawRecorderFactory` |
| Module / Plugin | `IOpenDeviceStudioModule` + 受信任进程内模块加载 | 模块内 `ConfigureServices`；仅从受信任位置加载 |
| Presentation | WPF Adapter 与可复用设备/参数/命令/告警控件 | 产品 UI 放在 `app/OpenDeviceStudio.App`，Core 不依赖 WPF |
| Testing | Simulator、FaultInjectingTransport、模块/Provider 测试基础设施 | 真实硬件前先覆盖 Simulator / Fault Path |

## 2. 二开从一个 Composition Root 开始

产品启动入口是 `app/OpenDeviceStudio.App/App.xaml.cs`。OpenDeviceStudio 先安装平台默认能力，再注册产品自己的服务：

```csharp
var builder = OpenDeviceStudioApplication
    .CreateBuilder(e.Args)
    .AddOpenDeviceStudioApplication();

builder.Services.AddSingleton<MainWindow>();
builder.Services.AddSingleton<IMyMachineService, MyMachineService>();
builder.Services.AddSingleton<IDevice, MyDevice>();

_host = builder.Build();
await _host.StartAsync();
```

原则：

- 平台基础设施由 OpenDeviceStudio 注册。
- 产品服务、设备、协议、业务流程在产品 Composition Root 注册。
- 不在业务代码里调用 Service Locator。
- 不为了“方便”建立静态全局单例。

## 3. DI：直接使用标准 Microsoft DI

OpenDeviceStudio 没有自造 DI Container。二开使用 `builder.Services`：

```csharp
builder.Services.AddSingleton<IMachineRuntime, MachineRuntime>();
builder.Services.AddTransient<ICommandFactory, CommandFactory>();
builder.Services.AddSingleton<IDevice, TemperatureController>();
```

业务类直接构造函数注入：

```csharp
public sealed class MachineRuntime(
    IDeviceRegistry devices,
    ILogger<MachineRuntime> logger)
{
    // ...
}
```

### 生命周期选择

- `Singleton`：设备 Runtime、Registry、长期连接管理、应用级协调服务。
- `Transient`：无状态、创建成本低的短生命周期对象。
- `Scoped`：仅在产品显式创建 Scope 时使用；WPF 桌面应用不会天然像 ASP.NET Request 一样自动创建 Scope。

如果要替换 OpenDeviceStudio 默认实现，应在 Composition Root 明确注册替代实现，并保持公共 Contract 不变；不要修改 Core 只为适配单个产品。

## 4. Configuration：产品配置走 Options Pattern

`OpenDeviceStudioApplication.CreateBuilder()` 基于 .NET Generic Host，因此可以使用标准 Configuration 来源。OpenDeviceStudio 自己的基础配置位于：

```text
OpenDeviceStudio:Transport
OpenDeviceStudio:Observability
```

产品配置建议建立自己的 Section：

```json
{
  "MyMachine": {
    "StationName": "Line-A",
    "HomeTimeoutSeconds": 30
  }
}
```

绑定并启动校验：

```csharp
builder.Services
    .AddOptions<MyMachineOptions>()
    .Bind(builder.Configuration.GetSection("MyMachine"))
    .Validate(
        options => options.HomeTimeoutSeconds > 0,
        "MyMachine:HomeTimeoutSeconds must be greater than 0.")
    .ValidateOnStart();
```

设备连接、端口、地址、超时等配置必须在启动阶段尽量 fail-fast，不要等设备开始动作后才发现配置错误。

## 5. Logging：业务代码只依赖 ILogger<T>

业务层不要直接依赖 Serilog API。统一使用：

```csharp
public sealed class AxisService(ILogger<AxisService> logger)
{
    public Task MoveAsync(string axisId, double position)
    {
        logger.LogInformation(
            "Axis {AxisId} moving to {Position}",
            axisId,
            position);

        return Task.CompletedTask;
    }
}
```

OpenDeviceStudio 当前默认日志链路：

```text
业务代码
  -> Microsoft.Extensions.Logging
      -> Console
      -> 可选 Serilog JSON Rolling File
```

默认文件日志由 `OpenDeviceStudio:Observability:Logging:File` 控制，例如：

```json
{
  "OpenDeviceStudio": {
    "Observability": {
      "Logging": {
        "File": {
          "Enabled": true,
          "Path": "logs/opendevicestudio-.json",
          "MinimumLevel": "Information"
        }
      }
    }
  }
}
```

如果产品需要 NLog、云日志、SIEM 或其他 Provider：

1. 业务代码仍保留 `ILogger<T>`。
2. 关闭不需要的 OpenDeviceStudio 文件日志。
3. 在 Composition Root 通过标准 `AddLogging(...)` 增加产品 Provider。
4. 不把具体日志框架类型泄漏到 Device / Protocol / Workflow 公共 Contract。

日志中禁止写入 Password、Token、设备 Secret、Private Key、原始 Connection String 等敏感信息。

## 6. Metrics、Tracing、Health：不要每个设备自己造一套

OpenDeviceStudio 提供统一 Observability 基线：

- Meter：`OpenDeviceStudio`。
- ActivitySource：`OpenDeviceStudio`。
- 可选 OpenTelemetry OTLP Export。
- `IHealthProbe` / `HealthService`。
- Transport、Command、Reconnect、Alarm、Streaming 等已有公共指标或观测点。

产品若需要自定义 Health：

```csharp
public sealed class RobotHealthProbe : IHealthProbe
{
    public string Name => "robot";

    public Task<HealthReport> CheckAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new HealthReport(
            Name,
            HealthStatus.Healthy,
            "Robot is ready."));
}

builder.Services.AddSingleton<IHealthProbe, RobotHealthProbe>();
```

指标必须保持低基数。DeviceId、ConnectionId、SessionId、CommandId 等逐次变化 ID 放到 Log / Trace，不要作为 Metric Attribute 无限扩张时间序列。

## 7. Hosted Service：后台循环交给 Host 管

设备发现、心跳、后台同步等长期任务不要塞进 Window Timer。使用标准 Hosted Service：

```csharp
builder.Services.AddHostedService<DeviceHeartbeatService>();
```

Hosted Service 必须：

- 支持 `CancellationToken`；
- 在停止时可确定性退出；
- 不创建无人管理的 fire-and-forget Task；
- 明确异常后的恢复或 Fault 行为。

## 8. Device 二开：实现能力，不扩巨型 BaseDevice

最小设备：

```csharp
public sealed class TemperatureController : IDevice
{
    public DeviceDescriptor Descriptor { get; } =
        new("temp-01", "Temperature Controller");

    public DeviceState State { get; private set; } = DeviceState.Offline;

    public IReadOnlyCollection<string> Capabilities { get; } =
        ["connect", "command", "parameter"];
}
```

再按设备真实能力组合：

```text
TemperatureController
  + IConnectable
  + ICommandable<SetTarget, SetTargetResult>
  + IParameterProvider

DAQ
  + IConnectable
  + IDataSource<SampleFrame>
```

注册：

```csharp
builder.Services.AddSingleton<IDevice, TemperatureController>();
```

Host 启动后会自动进入 `IDeviceRegistry`。

## 9. Transport / Protocol 二开

### 新 Transport

USB、CAN、BLE、厂商 SDK 等新连接方式：

1. 实现 `ITransport` 或与设备能力更匹配的 Provider Contract。
2. 不把业务命令、CRC 语义、UI、业务状态机塞进 Transport。
3. 使用统一注册入口 `AddOpenDeviceStudioTransport<TTransport>()`，复用 ConnectionManager、Resilience、Observability 等公共包装。

### 新 Protocol

Protocol 负责：

```text
领域 Command -> bytes
bytes -> 领域 Message / Result
```

实现：

```text
ICommandEncoder<TCommand>
IMessageDecoder<TMessage>
```

Decoder 必须处理分片、粘包、多帧、非法帧以及校验/长度失败后的恢复。

## 10. Control / Automation 二开

产品控制逻辑优先复用现有 Runtime，而不是在按钮事件里重新实现：

- Command：Composition Root 注册 `BoundedCommandDispatcher<TCommand,TResult>`
- Execution：Dispatcher 下层复用 `CommandRuntime<TCommand,TResult>` 的 Guard/完成语义
- Guard / Interlock：产品定义安全前置条件和 Command Safety Metadata
- Parameter：`IParameterProvider` + Readback，后续由 #63 复用同一资源仲裁
- Recipe：通过同一个 Dispatcher 下发设备修改，不再创建第二套 Scheduler
- Workflow：`WorkflowRunner`
- State：`StateMachine<TState,TTrigger>`
- Event：`IEventBus`
- Alarm：`IAlarmService`

典型调用链：

```text
WPF / Workflow
  -> Application Service
  -> Guard / State / Interlock
  -> Device Capability
  -> Protocol
  -> Transport
  -> Hardware
  -> Result / Completion / Readback
```

成功写出 bytes 不代表机械动作完成；产品必须定义真实完成条件。

## 11. Acquisition 二开

AddOpenDeviceStudioApplication() 已默认注册由 Host 持有的 AcquisitionSessionManager。每次 Live/Replay 运行创建一个冻结的 AcquisitionSessionDefinition，把 Source、Required、Optional adapter 交给同一个 Session；产品禁止再创建第二套采集生命周期协调器。

```csharp
var manager = services.GetRequiredService<AcquisitionSessionManager>();
await using var session = manager.CreateSession(new AcquisitionSessionDefinition(
    AcquisitionSessionMode.LiveAcquisition,
    sources,
    requiredComponents,
    optionalComponents));

await session.StartAsync(startRequestToken);
var result = await session.StopAsync();
```

高频数据热路径不经过复杂 Session orchestration：

```text
Hardware
 -> Transport / Provider
 -> Decoder / Canonical Raw
 -> Raw Recorder Accepted
 -> Processing
 -> bounded Router
 -> Algorithm / Processed Storage / UI
```

所有 Required Ready 后 Source 才能启动；Required fault 统一由 Session 收敛；Optional Presentation/Algorithm 默认隔离，只有冻结配置明确要求时才升级为 fatal。Replay Source 默认只读原 Raw artifact，复用同一 Processing implementation，并创建新的 ProcessingEpoch。

还必须明确 Channel / Queue Capacity、Backpressure、Drop/Loss Policy、UI 降采样以及原始数据保存与显示数据的职责边界。不要让 WPF UI Timer 成为采集时钟。

产品使用 `AddOpenDeviceStudioApplication()` 时 Raw Recording 默认开启，配置位于 `OpenDeviceStudio:Acquisition:RawRecording`。Queue Capacity、Session Quota、Segment 与 Durability 配置非法时启动阶段直接 fail-fast。替换默认存储时只注册一个 `IRawRecorderFactory`；显式关闭必须调用 `DisableRawRecording(reason)`，或配置 `Enabled=false` 且提供非空 `DisabledReason`，不能用“忘了注册 Recorder”作为关闭方式。

Source 通过 `RawFlowControl` 明确能力：只有 Provider 真正能够安全等待容量时才使用 `SupportsBackpressure`；不能阻塞的 SDK/Callback Source 使用 `CannotBackpressure`，Raw ingress 会强制走 `TryAccept`，过载直接让 Required Raw path Fault。

## 12. Storage 二开

简单产品可直接启用 JSON FileSystem：

```csharp
builder.AddFileSystemStorage("data");
```

跨产品需要 SQLite、SQL Server、时序数据库等实现时，实现稳定的 `IKeyValueStore` 或新增独立 Storage Provider，数据库 SDK 不进入 `OpenDeviceStudio.Abstractions`。

## 13. Module / Plugin 二开

需要模块化部署时实现：

```text
IOpenDeviceStudioModule
  -> ConfigureServices(IServiceCollection, IConfiguration)
```

Plugin 是**受信任的进程内扩展**，不是安全沙箱。不要加载未知来源程序集。

## 14. 测试二开

接真实设备前，优先使用：

- `SimulatorTransport`；
- Device Simulator；
- `FaultInjectingTransport`；
- Unit Test；
- Provider / Protocol Contract Test；
- Fault / Timeout / Cancellation / Reconnect Test。

产品代码新增可复用能力时，测试应和实现一起提交。

## 15. 哪些东西不应该由每个产品重写

二开项目通常**不应该重新造**：

- DI Container；
- 日志抽象；
- Host 生命周期；
- 配置加载框架；
- 通用 Connection Manager；
- Serial / TCP 基础 Transport；
- 通用 Command / Workflow / Event / Alarm Runtime；
- 通用 Metrics / Tracing / Health 基线；
- Simulator / Fault Injection 基础设施。

真正应该由产品实现的是：

- 设备语义；
- 私有协议；
- Command / Parameter；
- 设备完成条件和 Readback；
- 产品 Interlock；
- 工艺 Workflow；
- 产品 UI；
- 特定算法；
- 特定数据模型与持久化策略。

## 16. 推荐二开目录

```text
app/OpenDeviceStudio.App/
├─ Devices/          # 产品设备实现
├─ Protocols/        # 产品私有协议
├─ Services/         # Application Services
├─ Control/          # Command / Guard / Interlock
├─ Workflows/        # 产品流程
├─ Acquisition/      # 产品采集与算法编排
├─ Presentation/     # WPF 页面/ViewModel
├─ Options/          # 产品 Options
├─ App.xaml.cs       # Composition Root
└─ appsettings.json

src/OpenDeviceStudio.*/
└─ 只有确认跨产品复用的 Runtime / Provider / Infrastructure 才进入这里
```

## 17. 继续阅读

- [零基础入门](getting-started.zh-CN.md)
- [架构说明](architecture.zh-CN.md)
- [扩展 OpenDeviceStudio](extending.zh-CN.md)
- [Control Runtime](control-runtime.zh-CN.md)
- [Recipe 与 Scheduling](recipes-and-scheduling.zh-CN.md)
- [Observability](observability.zh-CN.md)
- [Provider 设计](providers.zh-CN.md)
