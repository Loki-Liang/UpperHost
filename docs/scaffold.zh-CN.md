# UpperHost 脚手架定位

简体中文 | [English](scaffold.md)

UpperHost 的官方定位是：

> **UpperHost：面向工业设备控制、自动化与数据采集的企业级 .NET 上位机开发脚手架。**

它的目标是给上位机开发者提供一套可直接复用的工程骨架和公共基础设施，让设备控制、自动化、数据采集类产品不再重复实现通信、生命周期、测试、诊断和扩展机制。

## 脚手架提供什么

UpperHost 同时服务三条一级开发路线：

- **Control**：命令、参数、读回、状态、联锁、诊断。
- **Automation**：多设备协同、状态机、Workflow、调度、告警、故障恢复。
- **Acquisition**：连续数据流、背压、存储、算法、显示。

脚手架包含：

- 模块化单体 Runtime；
- Device + Capability 契约；
- Transport / Protocol 扩展边界；
- Command / Control Runtime；
- Streaming / Dataflow 基础能力；
- Workflow / StateMachine 代码/API Runtime；
- Diagnostics / Alarm / Event / Storage / Resilience；
- Serial / TCP / Simulator 及可扩展 Provider seam；
- WPF Presentation Adapter 和可复用控件；
- 确定性 Simulator / Fault Injection；
- 自动化测试和架构治理；
- 可发布的 NuGet 模块；
- `dotnet new upperhost` 工程模板；
- 可运行 Reference Samples。

## 产品职责边界

使用 UpperHost 创建的具体产品负责自己的：

- Device 业务语义；
- 私有协议；
- Command / Parameter / Interlock 规则；
- 产品工作流；
- 产品 UI。

UpperHost 负责跨产品复用的 Runtime 契约、Provider、Template、Testing 接缝、Diagnostics 与 Presentation Adapter。

## 主要开发体验

推荐开发路径：

```text
dotnet new upperhost
        |
        v
生成上位机产品工程
        |
        +-- 产品 Device Capability
        +-- 产品 Protocol / Provider
        +-- 产品控制 / 采集业务
        +-- 产品 UI
        |
        v
Build / Test / Package
```

开发者应扩展脚手架，不应复制或重写 UpperHost Runtime。

## 架构基线

UpperHost 默认保持 **Modular Monolith**。

模块职责和依赖方向清晰，但生成的上位机产品默认仍作为一个应用/进程运行。

没有明确的产品或运维需求时，禁止为了“解耦”擅自引入微服务、远程 RPC 或分布式一致性。

## Presentation 边界

WPF 是当前默认 Presentation Adapter 和工程模板，不代表 UpperHost 本身只能是 WPF。

未来可以增加其他 Presentation Stack，但必须复用同一套 Runtime Contract。

未来提供的 Reference Presentation Application 统一复用 UpperHost Runtime Contract，仅用于展示具体产品如何组合脚手架模块。

## 交付规则

脚手架能力只有在可复用实现、测试、文档以及生成工程/外部消费方式一致时才算完成。

涉及具体硬件的支持声明必须区分 Simulator/Contract 证据与真实硬件验证。
