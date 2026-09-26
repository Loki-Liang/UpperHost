using System.Diagnostics;
using System.Diagnostics.Metrics;
using UpperHost.Abstractions.Observability;

namespace UpperHost.Acquisition;

internal static class AcquisitionTelemetry
{
    public static Counter<long> StateTransitions { get; } =
        UpperHostTelemetry.Meter.CreateCounter<long>(
            "upperhost.acquisition.session.transitions",
            "{transition}",
            "Acquisition session lifecycle transitions.");

    public static Counter<long> Faults { get; } =
        UpperHostTelemetry.Meter.CreateCounter<long>(
            "upperhost.acquisition.session.faults",
            "{fault}",
            "Acquisition session faults grouped by role-safe low-cardinality attributes.");

    public static UpDownCounter<long> ActiveSessions { get; } =
        UpperHostTelemetry.Meter.CreateUpDownCounter<long>(
            "upperhost.acquisition.session.active",
            "{session}",
            "Acquisition sessions currently in Running state.");

    public static Histogram<double> StopDuration { get; } =
        UpperHostTelemetry.Meter.CreateHistogram<double>(
            "upperhost.acquisition.session.stop_duration",
            "s",
            "Acquisition session convergence duration.");

    public static Counter<long> ClosedIngressRejections { get; } =
        UpperHostTelemetry.Meter.CreateCounter<long>(
            "upperhost.acquisition.ingress.rejected_closed",
            "{block}",
            "Raw blocks rejected because the owning acquisition session ingress was closed.");

    public static TagList StateTags(
        AcquisitionSessionMode mode,
        AcquisitionSessionState from,
        AcquisitionSessionState to)
    {
        var tags = new TagList
        {
            { "upperhost.acquisition.mode", mode.ToString() },
            { "upperhost.acquisition.from", from.ToString() },
            { "upperhost.acquisition.to", to.ToString() }
        };
        return tags;
    }

    public static TagList ModeTags(AcquisitionSessionMode mode)
    {
        var tags = new TagList
        {
            { "upperhost.acquisition.mode", mode.ToString() }
        };
        return tags;
    }

    public static TagList FaultTags(
        AcquisitionSessionMode mode,
        AcquisitionFaultCategory category,
        string role)
    {
        var tags = new TagList
        {
            { "upperhost.acquisition.mode", mode.ToString() },
            { "upperhost.acquisition.fault_category", category.ToString() },
            { "upperhost.acquisition.component_role", role }
        };
        return tags;
    }
}
