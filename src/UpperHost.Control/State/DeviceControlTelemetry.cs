using System.Diagnostics;
using System.Diagnostics.Metrics;
using UpperHost.Abstractions.Observability;

namespace UpperHost.Control.State;

internal static class DeviceControlTelemetry
{
    public static Counter<long> PollExecutions { get; } =
        UpperHostTelemetry.Meter.CreateCounter<long>(
            "upperhost.control.poll.executions",
            "{poll}",
            "Control polling executions by low-cardinality outcome.");

    public static Histogram<double> PollDurationSeconds { get; } =
        UpperHostTelemetry.Meter.CreateHistogram<double>(
            "upperhost.control.poll.duration",
            "s",
            "Control poll execution duration.");

    public static Counter<long> PollDeferred { get; } =
        UpperHostTelemetry.Meter.CreateCounter<long>(
            "upperhost.control.poll.deferred",
            "{poll}",
            "Polling ticks coalesced, skipped, or rejected by bounded scheduling.");

    public static Counter<long> SnapshotObservations { get; } =
        UpperHostTelemetry.Meter.CreateCounter<long>(
            "upperhost.control.snapshot.observations",
            "{observation}",
            "Snapshot observations accepted or rejected by the authoritative reducer.");

    public static Histogram<double> SnapshotAgeSeconds { get; } =
        UpperHostTelemetry.Meter.CreateHistogram<double>(
            "upperhost.control.snapshot.age",
            "s",
            "Age of snapshots read from the authoritative control state store.");

    public static Counter<long> ParameterOperations { get; } =
        UpperHostTelemetry.Meter.CreateCounter<long>(
            "upperhost.control.parameter.operations",
            "{operation}",
            "Typed parameter read/write outcomes.");

    public static Counter<long> EpochChanges { get; } =
        UpperHostTelemetry.Meter.CreateCounter<long>(
            "upperhost.control.epoch.changes",
            "{change}",
            "Connection epoch transitions observed by the control state runtime.");

    public static Counter<long> RehydrateResults { get; } =
        UpperHostTelemetry.Meter.CreateCounter<long>(
            "upperhost.control.rehydrate.results",
            "{result}",
            "Control rehydrate readiness outcomes.");

    public static TagList Tags(string operation, string outcome)
    {
        var tags = new TagList
        {
            { "upperhost.operation", operation },
            { "upperhost.result", outcome }
        };
        return tags;
    }

    public static TagList QualityTags(string operation, DeviceSnapshotQuality quality)
    {
        var tags = Tags(operation, "observed");
        tags.Add("upperhost.control.quality", quality.ToString());
        return tags;
    }
}
