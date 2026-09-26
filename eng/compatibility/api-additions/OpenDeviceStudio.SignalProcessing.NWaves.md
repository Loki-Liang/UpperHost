Issue: #59
Reason: Provide one mature reference DSP backend while keeping third-party DSP types out of the Core SignalProcessing public contract.
Surface: OpenDeviceStudio.SignalProcessing.NWaves adds NwavesMovingAverageStageFactory as a stateful per-partition online filter adapter over NWaves 0.9.6.
