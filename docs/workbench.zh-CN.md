# UpperHost Workbench 产品边界

简体中文 | [English](workbench.md)

UpperHost Workbench 是 UpperHost 面向最终用户的主要产品入口。它在同一套模块化单体 Runtime 之上提供开箱即用的设备应用体验，不建立第二套运行时。

## 产品目标

对于已经支持的设备，用户应走一条稳定的设备应用路径：

```text
Project
  -> Device
  -> Connection
  -> Command / Parameter / Streaming
  -> Data / Waveform / Alarm / Diagnostics
  -> Save
  -> Reopen
  -> 再次运行
```

Workbench 不是通用低代码平台。

## 明确非目标：可视化工作流编排

UpperHost **不建设**可视化 Workflow Editor、节点图、拖拽连线编程界面或通用低代码 Flow Engine。

现有 Workflow / StateMachine Runtime 继续作为代码/API 级自动化能力保留，但不扩展成图形化编排产品。

以下能力明确不在当前产品范围：

- Flow/Node Editor；
- 可视化 DAG 编辑；
- 拖拽连线执行图；
- Workflow Marketplace；
- 通用低代码表达式 Runtime；
- 从流程图生成业务代码。

未来若要引入以上任一能力，必须重新做产品决策并显式修改本边界，不能在普通 Feature 中顺带加入。

## 应用组合根

桌面主产品位于 `apps/UpperHost.Workbench.Wpf`。它属于 Application Composition Root，不是可复用平台模块：

- 可以组合 Starters、Presentation 和 Application Service；
- `src/` 生产模块禁止反向依赖 `apps/`；
- 可复用 WPF 控件继续保留在 `UpperHost.Presentation.Wpf`；
- 可复用 Workbench Application State 放在 `UpperHost.Workbench.Application`。

## Workbench 范围

Workbench 可以直接提供：

- Project 与工程生命周期；
- Device Catalog/Template；
- Connection 配置与所有权；
- Device 状态；
- Command 与 Command Result；
- Parameter 与 Readback；
- Streaming Data Monitor；
- Waveform/Numeric/Table；
- Alarm；
- Log；
- Diagnostics/Health；
- Storage 配置；
- Simulator/Fault Profile。

这些属于设备应用操作面，不属于流程编排面。

## 用户类型

### 配置型用户

使用已有 Device Template 和 Workbench 页面完成配置与运行，不修改平台源码。

### 设备集成开发者

当私有协议或 Vendor SDK 尚未支持时，开发新的 Device/Protocol/Provider/Simulator Package。

### 产品开发者

通过 UpperHost NuGet 和 Template 开发产品专用应用，同时复用相同 Runtime Contract。

## 交付入口

- **Workbench**：普通用户主要产品。
- **Runtime/NuGet**：平台模块和扩展契约的版本化交付方式。
- **dotnet new Template**：代码优先的二开入口。
- **Samples**：可运行参考，不建立独立 Runtime。

## 单一 Runtime 规则

Workbench、Templates、Tests 和产品应用必须消费同一套 Device/Control/Protocol/Dataflow/Diagnostics Contract。

禁止为了 Workbench 再复制一套 Device、Command、Connection 或 Streaming Runtime。

## Workbench 功能 Definition of Done

Workbench 功能不能因为“控件已经出现”或“API 已经存在”就视为完成。适用时必须同时包含：

- 生产实现；
- 与风险匹配的自动化测试；
- Simulator 路径；
- 可见的错误/Fault 状态；
- 涉及配置时的保存/重开；
- Windows CI 证据；
- 文档；
- 合并到 `main`。

涉及真实硬件的支持声明必须有独立 Hardware Validation Evidence；Simulator/Loopback 不能冒充真实硬件验证。
