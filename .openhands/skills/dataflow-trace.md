# Skill: Dataflow Trace

## Purpose

Prove an end-to-end acquisition/data pipeline is complete rather than a collection of local components.

## Method

Choose one real datum and trace it from source to every authoritative sink.

At every stage record:

- stage name and data type/meaning;
- producer and owner;
- thread/Task/execution context;
- buffer/queue/channel and capacity;
- copy/retain/release ownership;
- timestamp source;
- sequence/gap identity;
- transformation;
- overflow policy;
- failure propagation;
- cancellation/shutdown;
- persistence/replay semantics;
- observability.

For acquisition, trace at least:

    Device -> Transport -> Frame/Raw Record -> Raw Persistence -> Parse/Process -> Fan-out -> Algorithm/Display/Export

If a stage does not exist, explain why. Any stage whose ownership, capacity, error path, or data semantics cannot be explained is an architecture defect.
