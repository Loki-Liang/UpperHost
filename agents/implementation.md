# OpenDeviceStudio Implementation Agent

## Scope

Apply to every production-code change.

## Required flow

1. Read AGENTS.md plus all applicable specialty agents.
2. Confirm the accepted architecture, invariants, acceptance criteria, and Evidence Matrix before implementation.
3. Modify production code and risk-matched tests together.
4. Run focused validation.
5. Perform the Implementation Six-Kill audit and hostile contract checks.
6. Fix findings before Five-Gate Review.

## Implementation Six-Kill audit

Production implementation fails review if it contains an unjustified instance of:

1. **Unbounded resource:** queue, channel, buffer, retry, cache, batch, pool, task creation, or accumulation without a defined bound/saturation policy.
2. **Implicit state:** behavior depends on unrelated booleans, hidden static state, or undocumented state combinations rather than an explicit authority.
3. **Swallowed failure:** catch-and-ignore, lost exceptions, silent partial failure, or logs used instead of failure semantics.
4. **Unbounded retry:** retry/reconnect without cancellation, timeout, backoff, upper bound, or operator visibility.
5. **Ambiguous lifecycle:** unclear owner for start/stop/fault/dispose/subscription/native handles.
6. **Missing cancellation:** blocking or long-running I/O/work that cannot be cancelled when the caller/session stops.

## Engineering rules

- Dependencies are composed at application/Hosting/Starters composition roots. No mutable global singleton or Service Locator in platform/domain logic.
- I/O paths are async end-to-end. Avoid .Result, .Wait(), Thread.Sleep, and unmanaged fire-and-forget tasks.
- Socket, Stream, native handle, subscription, pooled buffer, and background worker ownership is explicit and deterministically released.
- Infrastructure/vendor errors are translated at module boundaries while retaining actionable context.
- Typed options/configuration validate early. Do not scatter magic environment reads, machine paths, or unvalidated string settings.
- Shared mutable state has an explicit owner or synchronization strategy.
- Add a NuGet/native dependency only with a documented reason and correct module placement.
- Prefer small cohesive types. Reject God Service, Manager-as-everything, utility dumping grounds, wrapper-on-wrapper indirection, and copy-pasted provider/protocol logic.
- Do not create an interface or abstraction only because “enterprise architecture” sounds cleaner. Keep it only when it protects a real boundary, test seam, or variability axis.
- Public API additions are deliberate compatibility decisions, not accidental exposure.

## Hostile interface check

Use .openhands/skills/contract-attack.md against public or stateful APIs. At minimum consider:

- duplicate Start/Stop/Dispose;
- Stop while Start is incomplete;
- concurrent calls;
- cancellation midway through work;
- invalid order;
- null/empty/oversized inputs;
- repeated commands or duplicate messages;
- caller retry after timeout;
- failure during cleanup.

The API must have deterministic behavior or explicitly reject unsupported usage.

## Minimum-correctness rule

After the implementation satisfies all invariants, failure semantics, and acceptance criteria, remove abstractions and layers that are not necessary to preserve them. If deleting a wrapper/manager/interface leaves behavior and boundaries unchanged, that abstraction requires a new justification or should be removed.

## Evidence

Implementation is not complete because code compiles. Evidence must come from agents.testing.md plus the applicable specialty rules.
