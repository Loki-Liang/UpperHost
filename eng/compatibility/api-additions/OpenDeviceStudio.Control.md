Issue: #60
Reason: Add the bounded command-dispatch, resource-arbitration, side-effect milestone, UnknownOutcome, connection-epoch and host-lifecycle contracts required by the enterprise Device Control route.
Surface: OpenDeviceStudio.Control adds BoundedCommandDispatcher, dispatch/admission/resource contracts, command safety/milestone/result extensions, contextual command execution, epoch validation, and DI/host registration APIs. Existing CommandRuntime constructor compatibility is preserved.

Issue: #63
Reason: Add the authoritative Device Snapshot, bounded Polling, typed Parameter read/write/readback, shared Control resource arbitration, connection-epoch invalidation and Rehydrate readiness contracts.
Surface: OpenDeviceStudio.Control adds DeviceSnapshotStore/Observation/Quality contracts, DevicePollRuntime/PollPlan contracts, TypedParameterRuntime/comparer/codec contracts, shared ICommandResourceArbiter and device-control state/rehydrate contracts.
