# Skill: Contract Attack

## Purpose

Test public/stateful APIs against hostile but legal callers.

## Method

Attack each applicable contract with:

- null/empty/oversized/malformed input;
- duplicate requests/commands/messages;
- concurrent calls;
- wrong call order;
- Start twice;
- Stop twice;
- Stop while Start is incomplete;
- cancellation halfway;
- timeout followed by caller retry;
- dependency failure after partial side effect;
- Dispose while work is active;
- use after stop/dispose.

The API must either support each behavior deterministically or reject it with a documented error. Hidden partial side effects, leaked work, and ambiguous state fail the audit.
