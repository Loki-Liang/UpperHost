# DataAcquisition Sample

[简体中文](README.zh-CN.md) | English

This sample demonstrates UpperHost's acquisition path as a first-class workload rather than the platform's center.

```text
SimulatorAcquisitionDevice (IDataSource<SampleFrame>)
        |
        v
   FanOutHub<SampleFrame>
      /             \
     v               v
Display consumer   Storage consumer
(slow/decimated)   (JSON Lines)
```

## What it proves

- An acquisition device exposes a typed async stream through `IDataSource<T>`.
- `FanOutHub<T>` separates producer speed from multiple consumers.
- The hub uses bounded per-subscriber buffers with an explicit `DropOldest` policy.
- A deliberately slow display consumer can lose old frames without blocking the producer or forcing storage code into the UI layer.
- Storage is an independent consumer seam; replace the JSON Lines consumer with SQLite, Parquet, a time-series database or a product-specific writer without changing the device.
- Display work is decimated independently from acquisition. Do not drive high-rate acquisition from a UI timer.

## Run

```powershell
dotnet run --project samples/UpperHost.Sample.DataAcquisition/UpperHost.Sample.DataAcquisition.csproj
```

The simulator produces 500 four-channel frames. The console prints produced/received counts and observed display sequence gaps so the backpressure behavior is visible.

## Production guidance

The sample intentionally keeps product policy outside the framework. A real product must explicitly choose buffer capacity, loss policy, storage batching, timestamp source, clock synchronization, reconnect behavior, file rotation, data integrity strategy and whether any consumer is allowed to apply backpressure to hardware.
