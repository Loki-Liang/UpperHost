# 扩展 UpperHost

简体中文 | [English](extending.md)

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

## 创建应用

安装模板后：

```powershell
dotnet new upperhost -n MyMachine --transport simulator
```

基础 Transport 选项：

```text
simulator
serial
tcp
```

其他 Provider 应独立交付，不修改 Core assembly。

第一次使用请先阅读：[零基础入门](getting-started.zh-CN.md)。
