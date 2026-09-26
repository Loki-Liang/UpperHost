Issue: #64
Reason: Add the route-level Acquisition Session authority required to coordinate Source, Raw Recorder, Processing, Router, optional presentation, fault convergence and replay composition without moving data-path implementations into the coordinator.
Surface: OpenDeviceStudio.Acquisition introduces session lifecycle/config/result contracts, required/optional component seams, source and replay contracts, source-isolation propagation, and the Raw-accepted-before-processing ingress guard.

Issue: #58
Reason: Let the Session authority consume the generic Raw Recorder contract without making the storage adapter a lifecycle authority or forcing Sources to locate recorder services themselves.
Surface: OpenDeviceStudio.Acquisition adds session-definition enrichment, default CanonicalRawBlock Raw-first ingress resolution, non-blocking raw sink capability, and a Required RawRecorder lifecycle adapter.
Surface: #58 also adds RawSourceFlowControl on IAcquisitionSource and propagates it through AcquisitionComponentContext/RawFirstAcquisitionIngress so CannotBackpressure sources use the non-blocking TryAccept Raw path.
