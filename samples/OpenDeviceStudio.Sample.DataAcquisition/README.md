# DataAcquisition Sample

[简体中文](README.zh-CN.md) | English

This sample demonstrates the Acquisition route under the **single #64 Session authority**.

```text
AcquisitionSessionManager
        |
        v
AcquisitionSession
  |              |
  | Required     | Source
  v              v
SampleDataflow <- SessionManagedSimulatorSource
  |                    |
  +--> Display          +--> SimulatorAcquisitionDevice
  +--> JSON Lines
```

## What it proves

- AcquisitionSession is the lifecycle authority; the Device, dataflow and presentation do not invent competing session state.
- Required dataflow is prepared before the Source is started and is finalized before the Session can become Completed.
- The Source owns its producer task and reports runtime failures through the Session fault seam; no unowned fire-and-forget task is created by the composition root.
- Host/source stop is explicit and the sample prints the terminal Session result.
- Display/storage remain separated from the Device implementation.

## Important boundary

The current FanOutHub + JSON Lines path is still the **legacy sample data path**. It is intentionally **not** labelled as the production Raw Recorder or final Signal Pipeline. #58, #56 and #59 remain responsible for lossless canonical Raw storage, routing QoS and signal processing. This sample only proves that those future route components now have one authoritative #64 lifecycle/composition root instead of owning independent start/stop state.

## Run

```powershell
dotnet run --project samples/OpenDeviceStudio.Sample.DataAcquisition/OpenDeviceStudio.Sample.DataAcquisition.csproj
```

The simulator produces 500 four-channel frames. The console reports the Session terminal state, produced frames, consumer counts and the storage artifact path.
