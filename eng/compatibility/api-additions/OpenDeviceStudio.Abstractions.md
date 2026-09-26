Issue: #63
Reason: Add the direct typed parameter access seam required by the Device State/Parameter runtime without forcing single-key reads to enumerate every descriptor.
Surface: OpenDeviceStudio.Abstractions adds IDirectParameterProvider for direct parameter reads while preserving the existing IParameterProvider contract.

Issue: #58
Reason: Define an industry-neutral Canonical RawData contract and recorder lifecycle without leaking FileStream, Arrow, EDF, transport chunks or product-specific signal semantics into Core.
Surface: OpenDeviceStudio.Abstractions adds immutable-owned CanonicalRawBlock metadata, source flow-control/durability/recorder state contracts, recorder factory/session interfaces and recovery reporting contracts.
