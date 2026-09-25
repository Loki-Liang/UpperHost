# 扩展 UpperHost

简体中文 | [English](extending.md)

UpperHost 是源码直接二开的上位机开发脚手架：扩展应增加可复用的 Device/Protocol/Provider 能力，具体产品业务继续放在 `app/UpperHost.App`。

## 增加 Device

实现 `IDevice`，然后只实现硬件真正支持的 Capability。通过 DI 注册，应用启动后由 `IDeviceRegistry` 暴露。

不要为了复用少量代码建立层层 `BaseDevice` 继承；优先使用组合和独立服务。

## 增加 Transport

实现 `ITransport`。

Transport 只负责连接和字节传输。以下内容不要放进 Transport：

- 业务命令；
- 帧头/帧尾语义；
- CRC/Checksum；
- 协议级重试；
- 产品状态机；
- UI 更新。

新的 USB/CAN/BLE/厂商 SDK 连接方式应作为独立 Provider/Starter package 发布。

## 复用 Provider Contract 与 Fault Test Kit

依赖硬件的 Provider 应直接复用 `UpperHost.Testing`，不要让每个二开产品重新造一套测试基础设施。

给任意 `ITransport` 套用确定性故障配置：

```csharp
var transport = new SimulatorTransport()
    .UseFaultProfile(new TransportFaultProfile(
        "disconnect-after-first-send",
        Seed: 42,
        DisconnectAfterSend: 1,
        FailReconnectAttempts: 1));
```

Profile 以操作计数/seed 为可复现依据，支持确定性 latency、timeout、send/receive failure、command reject、disconnect/reconnect failure、丢输入、数据破坏与 receive 分片。故障诊断会带上 profile 名、seed、endpoint 和操作序号。Latency 使用 `TimeProvider`，测试可推进虚拟时间，不需要用 `Thread.Sleep` 猜时序。

第一方或第三方字节流 Provider 在自己的测试项目实现 `ITransportContractFixture`，然后执行同一套公共契约：

```csharp
await TransportContractTestKit.VerifyAsync(
    cancellationToken => CreateMyProviderFixtureAsync(cancellationToken));
```

公共 Contract 校验 open/close/reopen 生命周期、send/receive 语义、取消、确定性释放与资源所有权。UpperHost 自己会让 Simulator、TCP loopback、Serial 测试通道运行同一 Contract。Simulator、loopback、fake-channel 只能算自动化测试证据，绝不能写成真实硬件验证结果。

Serial 的 `ISerialByteChannel` 是 `SerialTransport` 的 Provider 专属测试/适配 seam。产品代码默认仍使用 `System.IO.Ports`；测试可注入确定性的内存通道，不需要污染 `UpperHost.Abstractions`。

## 增加 Protocol

实现 `ICommandEncoder<TCommand>` 和/或 `IMessageDecoder<TMessage>`。

Decoder 拥有 framing 状态，必须处理：

- 分片输入；
- 多帧粘在一次 receive 中；
- 半包；
- 非法帧；
- 长度/校验失败后的恢复。

## 增加控制命令

优先使用强类型领域命令，而不是在 UI 中拼 `byte[]`：

```text
MoveAbsolute(100)
SetTargetTemperature(80)
Capture()
StartTest()
```

命令执行路径应明确状态校验、软件 Guard/Interlock、超时、取消、ACK、完成条件和 Readback。

软件 Guard/Interlock 不能替代硬件急停、安全 PLC 或认证安全回路。

## 增加 Workflow

把 `IWorkflowStep` 组合成 `WorkflowDefinition`。Workflow 描述产品行为，例如：

```text
connect -> self-test -> configure -> home -> run -> stop
```

每一步把硬件细节委托给 Device Capability/Service，而不是直接操作 Transport。

## 增加 Plugin

实现 `IUpperHostModule`，在 `ConfigureServices` 中注册依赖。把程序集部署到受信任的插件目录，并在 bootstrap 中调用 `AddModulesFromDirectory`。

Plugin 是受信任的进程内扩展，不是安全沙箱。

## 开始产品二开

拉取仓库后直接运行正式产品入口：

```powershell
git clone https://github.com/Loki-Liang/UpperHost.git MyMachine
cd MyMachine
dotnet run --project app/UpperHost.App/UpperHost.App.csproj
```

默认使用 `simulator`。需要真实设备时，在 `app/UpperHost.App/appsettings.json` 中切换到 `serial` 或 `tcp`；其他 Provider 继续通过现有 Provider/Starter 边界扩展。

第一次使用请先阅读：[零基础入门](getting-started.zh-CN.md)。

## Device Package、Descriptor 与 Catalog

可复用设备集成除了运行时 `IDevice` 实现外，还可以发布 `DevicePackageDescriptor`。Descriptor 是面向产品 App、Samples、CLI/Tooling 或具体产品 UI 的运行时无关元数据，描述 package/device 标识、package 版本、能力、支持的 Transport、Parameter/Command/Signal 元数据、强类型配置 Schema、可选 Simulator/Diagnostics 元数据以及 Provider/Protocol 依赖。

在组合阶段注册 Package：

```csharp
builder.AddDevicePackage(MyDevicePackage.Descriptor, new MyDevicePackageOptions());
```

`AddDevicePackage` 会立即通过 Package Schema 验证强类型配置。必填、范围、枚举/允许值以及 Package 自定义跨字段约束都必须返回 canonical configuration path；非法配置会在 Device 构建或任何 I/O 开始前 fail-fast。

Host 启动后可解析 `IDevicePackageCatalog`，枚举已安装 Descriptor，或按 capability、transport、vendor 查询。同一个 package id 按 `System.Version` 确定性选择最高版本，与注册顺序无关；相同 package id + version 出现不同 Descriptor 时抛出 `DevicePackageConflictException`。

Secret 字段必须使用 `DeviceSecretReference`，只描述外部 Secret 的来源和 key；明文凭据不得进入 Package metadata、普通日志或持久化 Descriptor 数据。

Catalog 是进程内的发现与注册服务。Package 获取、私有协议、Native SDK、Vendor Driver 和产品 UI 由消费应用及其 Provider/Device Package 负责。
