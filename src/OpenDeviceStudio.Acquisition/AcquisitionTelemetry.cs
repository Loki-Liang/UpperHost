using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenDeviceStudio.Abstractions.Observability;

namespace OpenDeviceStudio.Acquisition;

internal static class AcquisitionTelemetry
{
    public static Counter<long> StateTransitions { get; } =
        OpenDeviceStudioTelemetry.Meter.CreateCounter<long>(
            "opendevicestudio.acquisition.session.transitions",
            "{transition}",
            "Acquisition session lifecycle transitions.");

    public static Counter<long> Faults { get; } =
        OpenDeviceStudioTelemetry.Meter.CreateCounter<long>(
            "opendevicestudio.acquisition.session.faults",
            "{fault}",
            "Acquisition session faults grouped by role-safe low-cardinality attributes.");

    public static UpDownCounter<long> ActiveSessions { get; } =
        OpenDeviceStudioTelemetry.Meter.CreateUpDownCounter<long>(
            "opendevicestudio.acquisition.session.active",
            "{session}",
            "Acquisition sessions currently in Running state.");

    public static Histogram<double> StopDuration { get; } =
        OpenDeviceStudioTelemetry.Meter.CreateHistogram<double>(
            "opendevicestudio.acquisition.session.stop_duration",
            "s",
            "Acquisition session convergence duration.");

    public static Counter<long> ClosedIngressRejections { get; } =
        OpenDeviceStudioTelemetry.Meter.CreateCounter<long>(
            "opendevicestudio.acquisition.ingress.rejected_closed",
            "{block}",
            "Raw blocks rejected because the owning acquisition session ingress was closed.");

    public static TagList StateTags(
        AcquisitionSessionMode mode,
        AcquisitionSessionState from,
        AcquisitionSessionState to)
    {
        var tags = new TagList
        {
            { "opendevicestudio.acquisition.mode", mode.ToString() },
            { "opendevicestudio.acquisition.from", from.ToString() },
            { "opendevicestudio.acquisition.to", to.ToString() }
        };
        return tags;
    }

    public static TagList ModeTags(AcquisitionSessionMode mode)
    {
        var tags = new TagList
        {
            { "opendevicestudio.acquisition.mode", mode.ToString() }
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
            { "opendevicestudio.acquisition.mode", mode.ToString() },
            { "opendevicestudio.acquisition.fault_category", category.ToString() },
            { "opendevicestudio.acquisition.component_role", role }
        };
        return tags;
    }
}
