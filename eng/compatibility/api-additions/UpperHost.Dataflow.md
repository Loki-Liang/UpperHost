Issue: #56
Reason: Expose the production per-branch bounded streaming Router contract required to share one QoS, failure, ordering, ownership and observability model across Algorithms, Processed Storage and Presentation without copying queue/fan-out implementations.
Surface: UpperHost.Dataflow adds StreamRouter<T>, StreamBranchOptions, StreamPublishResult/StreamBranchPublishResult, Router/branch QoS and lifecycle enums/snapshots, and IStreamItemOwnership<T>. Legacy FanOutHub<T> remains available for compatibility but is no longer the production reference path.
