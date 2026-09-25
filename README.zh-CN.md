# UpperHost

[English](README.md) | 简体中文

**UpperHost：面向工业设备控制、自动化与数据采集的企业级 .NET 上位机开发脚手架。**

它为上位机开发提供可复用的工程骨架、Runtime、通信/协议边界、测试、诊断和扩展机制。具体产品负责自己的设备语义、私有协议、控制/联锁策略、业务工作流和产品 UI。UpperHost 同时服务三类常见上位机：

1. **设备控制（Control）**：命令、参数、读回、状态、联锁与诊断。
2. **自动化（Automation）**：多设备协同、状态机、工作流、告警与故障恢复。
3. **数据采集（Acquisition）**：连续数据流、背压、分发、存储、算法与显示。

## 先从你要做的应用开始

| 你要做什么 | 推荐入口 |
| --- | --- |
| 第一次使用 UpperHost | [零基础入门](docs/getting-started.zh-CN.md) |
| 理解脚手架定位 | [UpperHost 脚手架定位](docs/scaffold.zh-CN.md) |
| 控制 PLC、伺服、温控器、仪器等 | Device Control 路线 |
| 做自动工站、测试台、设备序列控制 | Automation 路线 |
| 做 EMG、DAQ、传感器、波形等实时采集 | Acquisition 路线 |
| 增加 TCP/串口之外的新通信方式 | [Provider 设计与接入](docs/providers.zh-CN.md) |
| 使用 OpenHands 开发仓库 | [OpenHands 接入](docs/openhands.zh-CN.md) |
| AI 开发总控规则 | [AGENTS.zh-CN.md](AGENTS.zh-CN.md) |
| 配置日志、Metrics、Tracing、Health | [Observability](docs/observability.zh-CN.md) |
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

## 5 分钟开始二次开发

需要 Windows 和 .NET 10 SDK。UpperHost 仓库本身就是可运行、可继续开发的上位机工程骨架，不需要先生成另一个项目。

```powershell
git clone https://github.com/Loki-Liang/UpperHost.git MyDeviceApp
cd MyDeviceApp
dotnet run --project app/UpperHost.App/UpperHost.App.csproj
```

首次启动默认使用 Simulator。接下来直接在当前产品仓库中增加自己的 Device、Protocol/Provider、Workflow、Acquisition 和产品 UI；可复用 Runtime 基础设施继续放在 `src/UpperHost.*`。

推荐二开边界：

```text
MyDeviceApp/
├─ app/UpperHost.App/          # 产品入口与产品 UI，二开从这里开始
├─ src/UpperHost.*/            # 可复用 Runtime / Provider / 基础设施
├─ samples/                    # 参考实现，不是产品入口
├─ tests/                      # Runtime 与基础设施测试
└─ UpperHost.slnx
```

需要接真实设备时，先阅读：[零基础入门：从 0 到第一台设备](docs/getting-started.zh-CN.md)。

如果你的目标是贡献 UpperHost Runtime 本身，而不是开发自己的上位机产品，再执行仓库级 `restore / build / test` 和贡献流程。

## 脚手架与产品的边界

UpperHost 脚手架负责：

- 应用生命周期、配置、DI、日志。
- 设备注册与发现。
- Transport、Protocol 公共运行时。
- `IConnectionManager` 通过 Shared/Exclusive Lease 统一管理物理连接 Open/Close，避免 Device、Workflow、UI 争用同一个 Serial/TCP/USB/Native Handle。
- Command/Streaming 的公共基础设施。
- Workflow、State Machine、Event、Alarm、Diagnostics。
- 企业级 Observability：结构化日志 Scope、滚动 JSON 文件日志、Metrics、Tracing、Transport Health、可选 OpenTelemetry OTLP。
- Dataflow、Storage、Testing、Plugin 扩展点。

业务产品负责：

- 自己的设备语义。
- 自己的协议格式。
- 自己的命令、参数、联锁规则和业务工作流。
- 自己的产品 UI。

## 当前路线图

脚手架基础能力此前按 GitHub Flow 分阶段推进：

- P0：中英文零基础文档、Device Control Sample。
- P1：Command Runtime、Interlock/Guard、Parameter Read/Write/Readback、AutomationStation Sample。
- P2：Recipe、Command Scheduler、DataAcquisition Sample。
- P3：USB/CAN/BLE/Vendor SDK Provider。

路线图与验收标准见 GitHub Issue #2。
