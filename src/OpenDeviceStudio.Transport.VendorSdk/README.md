# OpenDeviceStudio.Transport.VendorSdk

[简体中文](README.zh-CN.md) | English

`OpenDeviceStudio.Transport.VendorSdk` is the reference adapter for vendor SDKs that expose an **ordered byte-oriented session** but do not fit Serial/TCP/USB directly.

It intentionally does not reference any vendor DLL. Product code implements `IVendorByteSession`, keeping native handles, callbacks, thread-affinity rules and vendor exceptions outside Core.

## Byte-session template

```csharp
var options = new VendorSdkTransportOptions(
    VendorName: "Acme",
    DeviceAddress: "SN-001");

IVendorByteSession session = new AcmeSdkByteSession();
ITransport transport = new VendorSdkTransport(session, options);
```

`AcmeSdkByteSession` is the only layer that should reference the vendor SDK assembly/native library.

## Choose the correct seam first

Do **not** use this adapter merely because hardware ships with an SDK.

| Vendor SDK shape | Correct OpenDeviceStudio seam |
| --- | --- |
| Ordered raw bytes | `IVendorByteSession` + `VendorSdkTransport` |
| Discrete frames/messages | Provider-specific message type + `IMessageTransport<TMessage>` |
| Domain operations such as `Capture`, `MoveTo`, `Measure` | Device capability directly |

If the SDK already exposes domain operations, wrapping them into fake byte packets loses semantics and makes testing, state and completion rules less clear.

## Provider implementation checklist

1. Keep vendor/native references in the product provider package.
2. Translate native callbacks into the selected OpenDeviceStudio seam without blocking the callback thread.
3. Define who owns native handles and release them deterministically in `DisposeAsync`.
4. Map cancellation, timeout, disconnect and vendor error codes explicitly.
5. Preserve frame/message identity when the SDK is not actually byte-oriented.
6. Test with a fake backend/session before integrating real hardware.
7. Treat software disconnect/cancel as software behavior, not as a hardware emergency stop.

A production integration should normally live in a vendor-specific package such as `Company.Transport.Acme` or `Company.Device.AcmeCamera`; this package is the reusable reference template.
