using System.Diagnostics;
using System.Diagnostics.Metrics;
using UpperHost.Abstractions.Observability;

namespace UpperHost.Dataflow;

internal static class DataflowTelemetry
{
    public static UpDownCounter<long> ActiveBranches { get; } =
        UpperHostTelemetry.Meter.CreateUpDownCounter<long>(
            "upperhost.dataflow.branch.active",
            "{branch}",
            "Active StreamRouter branches.");

    public static UpDownCounter<long> QueueDepth { get; } =
        UpperHostTelemetry.Meter.CreateUpDownCounter<long>(
            "upperhost.dataflow.branch.queue_depth",
            "{item}",
            "Current bounded StreamRouter branch queue depth.");

    public static Counter<long> Accepted { get; } =
        UpperHostTelemetry.Meter.CreateCounter<long>(
            "upperhost.dataflow.branch.accepted",
            "{item}",
            "Items accepted by StreamRouter branches.");

    public static Counter<long> Delivered { get; } =
        UpperHostTelemetry.Meter.CreateCounter<long>(
            "upperhost.dataflow.branch.delivered",
            "{item}",
            "Items delivered successfully by StreamRouter branch consumers.");

    public static Counter<long> Dropped { get; } =
        UpperHostTelemetry.Meter.CreateCounter<long>(
            "upperhost.dataflow.branch.dropped",
            "{item}",
            "Items dropped by explicit lossy StreamRouter branch overflow policy.");

    public static Counter<long> Rejected { get; } =
        UpperHostTelemetry.Meter.CreateCounter<long>(
            "upperhost.dataflow.branch.rejected",
            "{item}",
            "Items rejected by StreamRouter branches.");

    public static Counter<long> Abandoned { get; } =
        UpperHostTelemetry.Meter.CreateCounter<long>(
            "upperhost.dataflow.branch.abandoned",
            "{item}",
            "Previously accepted items released without consumer delivery during fault/cancel shutdown.");

    public static Counter<long> Faults { get; } =
        UpperHostTelemetry.Meter.CreateCounter<long>(
            "upperhost.dataflow.branch.faults",
            "{fault}",
            "StreamRouter branch faults.");

    public static Counter<long> RouterFaults { get; } =
        UpperHostTelemetry.Meter.CreateCounter<long>(
            "upperhost.dataflow.router.faults",
            "{fault}",
            "StreamRouter faults caused by required or explicitly escalating branches.");

    public static Histogram<double> EnqueueLatency { get; } =
        UpperHostTelemetry.Meter.CreateHistogram<double>(
            "upperhost.dataflow.branch.enqueue_latency",
            "s",
            "Elapsed time for a branch to accept an item, including required Wait backpressure.");

    public static Histogram<double> ConsumerLag { get; } =
        UpperHostTelemetry.Meter.CreateHistogram<double>(
            "upperhost.dataflow.branch.consumer_lag",
            "s",
            "Elapsed time from publish attempt to branch consumer dequeue.");

    public static TagList BranchTags(StreamBranchOptions options)
    {
        var tags = new TagList
        {
            { "upperhost.dataflow.branch", options.BranchId },
            { "upperhost.dataflow.delivery", options.Delivery.ToString() },
            { "upperhost.dataflow.overflow", options.Overflow.ToString() }
        };
        return tags;
    }

    public static TagList RouterFaultTags(StreamBranchDelivery delivery)
    {
        var tags = new TagList
        {
            { "upperhost.dataflow.delivery", delivery.ToString() }
        };
        return tags;
    }
}
