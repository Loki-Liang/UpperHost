using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenDeviceStudio.Abstractions.Observability;

namespace OpenDeviceStudio.Control.Scheduling;

internal static class CommandDispatcherTelemetry
{
    public static Counter<long> AdmissionRejections { get; } =
        OpenDeviceStudioTelemetry.Meter.CreateCounter<long>(
            "opendevicestudio.command.dispatcher.admission_rejections",
            "{rejection}",
            "Commands rejected before entering or completing dispatcher admission.");

    public static UpDownCounter<long> PendingCommands { get; } =
        OpenDeviceStudioTelemetry.Meter.CreateUpDownCounter<long>(
            "opendevicestudio.command.dispatcher.pending",
            "{command}",
            "Commands currently pending in bounded dispatcher lanes.");

    public static Histogram<double> QueueWaitSeconds { get; } =
        OpenDeviceStudioTelemetry.Meter.CreateHistogram<double>(
            "opendevicestudio.command.dispatcher.queue_wait",
            "s",
            "Elapsed time from enqueue attempt to dequeue for execution.");

    public static Histogram<double> ResourceWaitSeconds { get; } =
        OpenDeviceStudioTelemetry.Meter.CreateHistogram<double>(
            "opendevicestudio.command.dispatcher.resource_wait",
            "s",
            "Elapsed time waiting for resource arbitration.");

    public static Counter<long> EpochInvalidations { get; } =
        OpenDeviceStudioTelemetry.Meter.CreateCounter<long>(
            "opendevicestudio.command.dispatcher.epoch_invalidations",
            "{command}",
            "Commands rejected because their connection epoch became stale.");

    public static TagList PriorityTags(CommandPriority priority)
    {
        var tags = new TagList
        {
            { "opendevicestudio.command.priority", priority.ToString() }
        };
        return tags;
    }

    public static TagList ResultTags(CommandPriority priority, string result)
    {
        var tags = PriorityTags(priority);
        tags.Add("opendevicestudio.result", result);
        return tags;
    }
}
