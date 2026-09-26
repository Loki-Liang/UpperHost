Issue: #60
Reason: Add the bounded command-dispatch, resource-arbitration, side-effect milestone, UnknownOutcome, connection-epoch and host-lifecycle contracts required by the enterprise Device Control route.
Surface: UpperHost.Control adds BoundedCommandDispatcher, dispatch/admission/resource contracts, command safety/milestone/result extensions, contextual command execution, epoch validation, and DI/host registration APIs. Existing CommandRuntime constructor compatibility is preserved.
