Issue: #63
Reason: Add the direct typed parameter access seam required by the Device State/Parameter runtime without forcing single-key reads to enumerate every descriptor.
Surface: OpenDeviceStudio.Abstractions adds IDirectParameterProvider for direct parameter reads while preserving the existing IParameterProvider contract.
