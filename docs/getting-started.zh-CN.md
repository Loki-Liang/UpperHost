# UpperHost 零基础入门

简体中文 | [English](getting-started.md)

这份文档面向第一次使用 UpperHost、甚至第一次开发上位机的开发者。目标不是先理解框架源码，而是从空环境走到一个可运行的 Simulator 设备应用，并知道下一步代码应该放在哪里。

## 1. 先理解 UpperHost 是什么

UpperHost 是**通用设备应用平台**，三条路线地位相同：

- **控制 Control**：命令、参数、读回、状态、联锁、诊断。
- **自动化 Automation**：多设备协同、状态机、工作流、告警、故障恢复。
- **采集 Acquisition**：连续数据流、Dataflow、背压、存储、算法、显示。

采集只是设备能力的一类，不是整个架构的中心。

## 2. 安装环境

需要：

- WPF 应用运行在 Windows。
- .NET 10 SDK。
- Git。
- Visual Studio、Rider、VS Code 任意一种都可以；只使用命令行也可以。

验证：

```powershell
dotnet --info
git --version
```

## 3. 先确认 UpperHost 本身能构建

```powershell
git clone https://github.com/Loki-Liang/UpperHost.git
cd UpperHost
dotnet restore UpperHost.slnx
dotnet build UpperHost.slnx -c Release
dotnet test tests/UpperHost.Tests/UpperHost.Tests.csproj -c Release
```

如果这里失败，先解决 SDK/环境问题，不要急着写设备代码。

## 4. 安装项目模板

```powershell
dotnet pack templates/UpperHost.Templates.csproj -c Release -o artifacts/packages
dotnet new install artifacts/packages/UpperHost.Templates.0.1.0-alpha.1.nupkg
```

建议在 UpperHost 仓库外创建第一个项目：

```powershell
cd ..
dotnet new upperhost -n MyFirstUpperHostApp --transport simulator
cd MyFirstUpperHostApp
dotnet run
```

能看到生成的 WPF 窗口，说明 Hosting、DI、Configuration 和基础 Transport Starter 已经接通。

## 5. 写代码前只需要先理解 5 个概念

### Device：这是什么设备

`IDevice` 只描述设备身份、状态和能力。真实功能通过 Capability 组合。

例如：

```text
温控器 TemperatureController
  + IConnectable
  + ICommandable<SetTarget, Result>
  + IParameterProvider

相机 Camera
  + IConnectable
  + ICommandable<Capture, Image>

采集卡 DAQ
  + IConnectable
  + IDataSource<SampleFrame>
```

不要为所有设备建立一个越来越大的 `BaseDevice` 继承树。

### Transport：字节怎么到设备

Transport 只处理连接和字节传输，例如：

```text
Serial / TCP / USB / CAN / BLE / Vendor SDK Adapter
```

Transport 不应该知道“电机回零”“开始采样”“设置温度”是什么意思。

### Protocol：这些字节是什么意思

Protocol 负责：

```text
领域命令 -> byte[]
byte[] -> 领域消息
```

帧头、长度、CRC、粘包/拆包、命令码、字段解释都属于协议层。

### Application Behavior：现在允许什么，下一步做什么

- State Machine：回答“当前允许做什么”。
- Workflow：回答“接下来按什么步骤做”。

### Presentation：用户看到什么

WPF 是适配层。UI 可以发起命令、展示状态，但不应该拥有 Socket Receive Loop、协议拆帧、设备生命周期的权威状态。

## 6. 第一次接真实设备，按这个顺序

1. 定义 `DeviceDescriptor`，实现 `IDevice`。
2. 如果设备支持连接/断开，实现 `IConnectable`。
3. 把一个真实业务动作建模成强类型 Command。
4. 在 Protocol 层实现命令编码和响应解码。
5. 能模拟的先用 Simulator 验证协议和业务路径。
6. 协议可测试后再接 TCP/Serial/USB/CAN/厂商 SDK。
7. 通过 DI 注册 Device。
8. UI/Workflow 只调用 Device Capability。
9. 明确超时、取消、故障后的状态变化。
10. 产品特定联锁写在产品/控制层，不塞进 Transport。

## 7. 根据你的上位机选择路线

### A. 设备控制

适合 PLC、伺服、温控器、电源、实验仪器、执行器等。

```text
UI / Workflow
  -> Command
  -> Guard / State Validation
  -> Device Capability
  -> Protocol
  -> Transport
  -> Hardware
  -> Ack / Result / Readback
```

### B. 自动化设备

多台设备需要按流程协同时：

```text
PLC Ready
 -> Axis Home
 -> Move
 -> Camera Capture
 -> Judge
 -> PLC Result
```

使用 Device Capability + State Machine + Workflow，而不是把整段流程写进一个按钮事件。

### C. 数据采集

适合 EMG、DAQ、摄像头、传感器连续数据、实时波形等：

```text
Hardware
 -> Transport
 -> Decoder
 -> Stream
 -> Dataflow
 -> Storage / Algorithm / UI
```

低频控制设备不要强行走 Streaming；高频采集也不要靠 UI Timer 拉数据。

## 8. 配置从 Simulator 开始

模板从 `appsettings.json` 的 `UpperHost:Transport` 读取基础 Transport 配置。

第一次开发优先使用 `simulator`。只有需要真实硬件时，再切换 `tcp`、`serial` 或安装其他 Provider。

平台级必填配置错误应该启动即失败，并指出具体配置键，而不是机器开始运行后才报错。

## 9. 每一个设备命令都要回答这些问题

- 超时多久？
- 能否取消？
- 是否允许重试？
- 重复发送是否安全？
- 失败后设备进入什么状态？
- 收到 ACK 算完成，还是物理动作真正结束才算完成？
- 是否必须 Readback 验证？

`SendAsync` 成功只说明字节写出去了，不能证明机械动作或设备任务完成。

## 10. 软件安全边界

UpperHost 可以承载软件层 Guard、Interlock、状态验证和业务保护，但**不能代替**：

- 硬件急停；
- 安全继电器；
- Safety PLC；
- 经认证的硬件安全回路。

软件层负责减少错误操作和协调状态，硬件安全链仍然是最终安全权威。

## 11. 下一步阅读

- [架构说明](architecture.zh-CN.md)
- [扩展 UpperHost](extending.zh-CN.md)
- Device Control Sample（P0）
- AutomationStation Sample（P1）
- DataAcquisition Sample（P2）
