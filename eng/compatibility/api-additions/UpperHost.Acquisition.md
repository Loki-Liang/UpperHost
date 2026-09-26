Issue: #64
Reason: Add the route-level Acquisition Session authority required to coordinate Source, Raw Recorder, Processing, Router, optional presentation, fault convergence and replay composition without moving data-path implementations into the coordinator.
Surface: UpperHost.Acquisition introduces session lifecycle/config/result contracts, required/optional component seams, source and replay contracts, source-isolation propagation, and the Raw-accepted-before-processing ingress guard.
