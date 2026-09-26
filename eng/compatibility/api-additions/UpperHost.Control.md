# UpperHost.Control public API additions — Issue #60

Issue: #60

Category: Additive

Reason: Introduce the stable command-dispatch contracts required by the bounded dispatcher, shared resource arbitration, safety policy, and unknown-outcome semantics. Existing CommandRuntime/CommandScheduler APIs remain available for compatibility while the new runtime is introduced.

Surface:
- CommandAdmissionMode
- CommandAccessMode
- CommandIdempotency
- CommandHazardClass
- CommandSideEffectState
- CommandResourceKind
- CommandResourceKey
- CommandResourceSet
- CommandSafetyPolicy
- CommandDispatcherOptions
- CommandExecutionStatus.UnknownOutcome

Compatibility: additive only; no existing member is removed or renamed.
