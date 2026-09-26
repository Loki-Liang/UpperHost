using System.Diagnostics;
using System.Diagnostics.Metrics;
using UpperHost.Abstractions.Observability;

namespace UpperHost.Control.Scheduling;

internal static class CommandDispatcherTelemetry
{
    public static Counter<long> AdmissionRejections { get; } =
        UpperHostTelemetry.Meter.CreateCounter<long>(
            "upperhost.command.dispatcher.admission_rejections",
            "{rejection}",
            "Commands rejected before entering or completing dispatcher admission.");

    public static UpDownCounter<long> PendingCommands { get; } =
        UpperHostTelemetry.Meter.CreateUpDownCounter<long>(
            "upperhost.command.dispatcher.pending",
            "{command}",
            "Commands currently pending in bounded dispatcher lanes.");

    public static Histogram<double> QueueWaitSeconds { get; } =
        UpperHostTelemetry.Meter.CreateHistogram<double>(
            "upperhost.command.dispatcher.queue_wait",
            "s",
            "Elapsed time from enqueue attempt to dequeue for execution.");

    public static Histogram<double> ResourceWaitSeconds { get; } =
        UpperHostTelemetry.Meter.CreateHistogram<double>(
            "upperhost.command.dispatcher.resource_wait",
            "s",
            "Elapsed time waiting for resource arbitration.");

    public static Counter<long> EpochInvalidations { get; } =
        UpperHostTelemetry.Meter.CreateCounter<long>(
            "upperhost.command.dispatcher.epoch_invalidations",
            "{command}",
            "Commands rejected because their connection epoch became stale.");

    public static TagList PriorityTags(CommandPriority priority)
    {
        var tags = new TagList
        {
            { "upperhost.command.priority", priority.ToString() }
        };
        return tags;
    }

    public static TagList ResultTags(CommandPriority priority, string result)
    {
        var tags = PriorityTags(priority);
        tags.Add("upperhost.result", result);
        return tags;
    }
}
