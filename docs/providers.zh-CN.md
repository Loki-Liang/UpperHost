# Transport Provider 设计与接入

简体中文 | [English](providers.md)

OpenDeviceStudio 不把所有通信方式强行伪装成 byte stream。Provider 必须保留底层通信技术本来的消息边界和语义形态。

## 先选择正确的抽象

| 通信形态 | OpenDeviceStudio seam | 示例 |
| --- | --- | --- |
| 有序字节流 | `ITransport` | Serial、TCP、USB Bulk Endpoint、字节型厂商 SDK |
| 离散 Frame / Message | `IMessageTransport<TMessage>` | CAN Frame、BLE GATT 操作/通知、Datagram |
| 厂商 SDK 已经直接提供领域动作 | 直接实现 Device Capability | 相机 `Capture`、机器人 `Move`、仪器 `Measure` |

`IMessageTransport<TMessage>` 与 `ITransport` 共享 Open/Close/State 生命周期，但保留 Message 边界。具体 `TMessage` 定义必须放在 Provider 包中，不能把 CAN/BLE 类型塞进 `OpenDeviceStudio.Abstractions`。

## Provider 硬约束

1. Native/厂商依赖只能留在 Provider 包。
2. Core 禁止引用 libusb、CAN Driver、BLE Stack、厂商 SDK DLL。
3. Provider 只负责连接和传输机制，不承载产品业务命令。
4. Protocol / Device 层负责语义解释和领域动作。
5. Cancellation、Timeout、Disconnect 行为必须显式。
6. 必须提供稳定 Backend seam，测试时可以替换 Native Driver。
7. 软件 Priority、Cancellation、Disconnect 都不能宣称等价于硬件急停。

## P3 Provider

- `OpenDeviceStudio.Transport.Usb`：USB 字节型 Provider，由可注入 USB Channel Backend 承载具体 libusb/WinUSB/厂商实现。
- `OpenDeviceStudio.Transport.Can`：实现 `IMessageTransport<CanFrame>`，保留 Arbitration ID、Extended ID、RTR 和 Data 边界。
- `OpenDeviceStudio.Transport.Ble`：实现 `IMessageTransport<BleMessage>`，保留 Service/Characteristic 与 Notification/Write 等 GATT 边界。
- `OpenDeviceStudio.Transport.VendorSdk`：提供字节型 Vendor SDK Adapter，同时明确“SDK 已经是领域 API”时应直接实现 Device Capability，而不是伪装 Transport。

## Vendor SDK 统一决策模板

写 Adapter 前先分类厂商 API：

1. **有序原始字节** → 实现 `IVendorByteSession`，使用 `VendorSdkTransport`。
2. **离散 Frame / Message API** → Provider 自己定义消息类型，实现 `IMessageTransport<TMessage>`。
3. **领域 API** → 直接实现 Device Capability，禁止制造虚假的 Transport byte[]。

厂商 Provider 包负责 Native DLL 引用、Handle 生命周期、Callback/线程亲和性规则以及厂商错误码映射；Core 始终保持厂商无关。可复制模板与检查表见 [OpenDeviceStudio.Transport.VendorSdk](../src/OpenDeviceStudio.Transport.VendorSdk/README.zh-CN.md)。

每个 Provider 都按独立 GitHub Flow PR 交付，并以 Build/Test 证据闭环。
