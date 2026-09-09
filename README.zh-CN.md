# UpperHost

[English](README.md) | 简体中文

**UpperHost 是用于构建上位机 / 面向设备应用的通用 .NET 平台。**

它不是采集框架，也不绑定医疗设备、PLC、相机、机器人、实验仪器或任何单一行业。UpperHost 的目标是为三类常见设备应用提供统一、可扩展的基础设施：

1. **设备控制（Control）**：命令、参数、读回、状态、联锁与诊断。
2. **自动化（Automation）**：多设备协同、状态机、工作流、告警与故障恢复。
3. **数据采集（Acquisition）**：连续数据流、背压、分发、存储、算法与显示。

## 先从你要做的应用开始

| 你要做什么 | 推荐入口 |
| --- | --- |
| 第一次使用 UpperHost | [零基础入门](docs/getting-started.zh-CN.md) |
| 控制 PLC、伺服、温控器、仪器等 | Device Control 路线 |
| 做自动工站、测试台、设备序列控制 | Automation 路线 |
| 做 EMG、DAQ、传感器、波形等实时采集 | Acquisition 路线 |
| 增加 TCP/串口之外的新通信方式 | [扩展 UpperHost](docs/extending.zh-CN.md) |
| 理解平台边界 | [架构说明](docs/architecture.zh-CN.md) |

## 核心架构

```text
Presentation (WPF / WinUI / Avalonia / CLI / Service)
        |
Application / Workflow / State Machine / Event Bus
        |
Device Registry + Discovery + Capability Composition
        |
+---------------------+---------------------+
| Command / Parameter |     Streaming       |
| Request / Response  | Dataflow/Backpressure|
+---------------------+---------------------+
        |
Protocol
        |
Transport (Serial / TCP / USB / CAN / BLE / Vendor SDK / ...)
        |
Hardware
```

### 关键设计规则

- Core 必须保持行业无关。
- 设备通过 Capability 组合能力，而不是建立巨型继承树。
- Transport 只负责传输；Protocol 负责帧和语义。
- Request/Response 与 Streaming 都是一等模型。
- WPF 是适配层，不是 Core。
- 软件联锁/安全策略只负责软件控制约束，不能替代硬件急停、安全 PLC 或认证安全回路。
- 厂商 SDK、USB、CAN、BLE 等通过独立 Provider/Starter 扩展，不进入 `UpperHost.Abstractions`。

## 创建第一个项目

需要 .NET 10 SDK；WPF 项目需要 Windows。

```powershell
dotnet restore UpperHost.slnx
dotnet build UpperHost.slnx -c Release
dotnet test tests/UpperHost.Tests/UpperHost.Tests.csproj -c Release
```

安装本地模板：

```powershell
dotnet pack templates/UpperHost.Templates.csproj -c Release -o artifacts/packages
dotnet new install artifacts/packages/UpperHost.Templates.0.1.0-alpha.1.nupkg
```

创建 Simulator 项目：

```powershell
dotnet new upperhost -n MyDeviceApp --transport simulator
cd MyDeviceApp
dotnet run
```

然后继续阅读：[零基础入门：从 0 到第一台设备](docs/getting-started.zh-CN.md)。

## 平台与产品的边界

UpperHost 负责：

- 应用生命周期、配置、DI、日志。
- 设备注册与发现。
- Transport、Protocol 公共运行时。
- Command/Streaming 的公共基础设施。
- Workflow、State Machine、Event、Alarm、Diagnostics。
- Dataflow、Storage、Testing、Plugin 扩展点。

业务产品负责：

- 自己的设备语义。
- 自己的协议格式。
- 自己的命令、参数、联锁规则和业务工作流。
- 自己的产品 UI。

## 当前路线图

平台按 GitHub Flow 分阶段推进：

- P0：中英文零基础文档、Device Control Sample。
- P1：Command Runtime、Interlock/Guard、Parameter Read/Write/Readback、AutomationStation Sample。
- P2：Recipe、Command Scheduler、DataAcquisition Sample。
- P3：USB/CAN/BLE/Vendor SDK Provider。

路线图与验收标准见 GitHub Issue #2。
