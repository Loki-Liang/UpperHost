using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenDeviceStudio.Abstractions.Observability;

namespace OpenDeviceStudio.Control.State;

internal static class DeviceControlTelemetry
{
    public static Counter<long> PollExecutions { get; } =
        OpenDeviceStudioTelemetry.Meter.CreateCounter<long>(
            "opendevicestudio.control.poll.executions",
            "{poll}",
            "Control polling executions by low-cardinality outcome.");

    public static Histogram<double> PollDurationSeconds { get; } =
        OpenDeviceStudioTelemetry.Meter.CreateHistogram<double>(
            "opendevicestudio.control.poll.duration",
            "s",
            "Control poll execution duration.");

    public static Counter<long> PollDeferred { get; } =
        OpenDeviceStudioTelemetry.Meter.CreateCounter<long>(
            "opendevicestudio.control.poll.deferred",
            "{poll}",
            "Polling ticks coalesced, skipped, or rejected by bounded scheduling.");

    public static Counter<long> SnapshotObservations { get; } =
        OpenDeviceStudioTelemetry.Meter.CreateCounter<long>(
            "opendevicestudio.control.snapshot.observations",
            "{observation}",
            "Snapshot observations accepted or rejected by the authoritative reducer.");

    public static Histogram<double> SnapshotAgeSeconds { get; } =
        OpenDeviceStudioTelemetry.Meter.CreateHistogram<double>(
            "opendevicestudio.control.snapshot.age",
            "s",
            "Age of snapshots read from the authoritative control state store.");

    public static Counter<long> ParameterOperations { get; } =
        OpenDeviceStudioTelemetry.Meter.CreateCounter<long>(
            "opendevicestudio.control.parameter.operations",
            "{operation}",
            "Typed parameter read/write outcomes.");

    public static Counter<long> EpochChanges { get; } =
        OpenDeviceStudioTelemetry.Meter.CreateCounter<long>(
            "opendevicestudio.control.epoch.changes",
            "{change}",
            "Connection epoch transitions observed by the control state runtime.");

    public static Counter<long> RehydrateResults { get; } =
        OpenDeviceStudioTelemetry.Meter.CreateCounter<long>(
            "opendevicestudio.control.rehydrate.results",
            "{result}",
            "Control rehydrate readiness outcomes.");

    public static TagList Tags(string operation, string outcome)
    {
        var tags = new TagList
        {
            { "opendevicestudio.operation", operation },
            { "opendevicestudio.result", outcome }
        };
        return tags;
    }

    public static TagList QualityTags(string operation, DeviceSnapshotQuality quality)
    {
        var tags = Tags(operation, "observed");
        tags.Add("opendevicestudio.control.quality", quality.ToString());
        return tags;
    }
}
