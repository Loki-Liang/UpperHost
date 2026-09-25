using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace UpperHost.Abstractions.Observability;

public sealed record UpperHostTelemetryContext(
    string? DeviceId = null,
    string? ConnectionId = null,
    string? SessionId = null,
    string? CommandId = null,
    string? Protocol = null,
    string? Transport = null,
    string? Operation = null,
    string? Result = null,
    string? ErrorCode = null);

public sealed record UpperHostMetricContext(
    string? Protocol = null,
    string? Transport = null,
    string? Operation = null,
    string? Outcome = null,
    string? ErrorType = null,
    string? AlarmSeverity = null);

public static class UpperHostTelemetry
{
    public const string InstrumentationName = "UpperHost";
    public const string InstrumentationVersion = "0.1.0";

    public static ActivitySource ActivitySource { get; } =
        new(InstrumentationName, InstrumentationVersion);

    public static Meter Meter { get; } =
        new(InstrumentationName, InstrumentationVersion);

    public static Counter<long> CommandExecutions { get; } =
        Meter.CreateCounter<long>("upperhost.command.executions", "{execution}",
            "Number of command executions by command type and outcome.");

    public static Counter<long> CommandFailures { get; } =
        Meter.CreateCounter<long>("upperhost.command.failures", "{failure}",
            "Number of non-successful command executions.");

    public static Histogram<double> CommandDurationSeconds { get; } =
        Meter.CreateHistogram<double>("upperhost.command.duration", "s",
            "Command execution duration in seconds.");

    public static Counter<long> TransportOperations { get; } =
        Meter.CreateCounter<long>("upperhost.transport.operations", "{operation}",
            "Number of transport operations.");

    public static Counter<long> TransportFailures { get; } =
        Meter.CreateCounter<long>("upperhost.transport.failures", "{failure}",
            "Number of failed transport operations.");

    public static Counter<long> TransportBytes { get; } =
        Meter.CreateCounter<long>("upperhost.transport.bytes", "By",
            "Bytes transferred through UpperHost transports.");

    public static Counter<long> ReconnectAttempts { get; } =
        Meter.CreateCounter<long>("upperhost.transport.reconnect.attempts", "{attempt}",
            "Transport reconnect attempts.");

    public static Counter<long> ConnectionOperations { get; } =
        Meter.CreateCounter<long>("upperhost.connection.operations", "{operation}",
            "Number of managed connection lifecycle operations.");

    public static Counter<long> ConnectionFailures { get; } =
        Meter.CreateCounter<long>("upperhost.connection.failures", "{failure}",
            "Number of failed managed connection lifecycle operations.");

    public static UpDownCounter<long> ActiveConnectionLeases { get; } =
        Meter.CreateUpDownCounter<long>("upperhost.connection.active_leases", "{lease}",
            "Current active managed connection leases.");

    public static UpDownCounter<long> ActiveAlarms { get; } =
        Meter.CreateUpDownCounter<long>("upperhost.alarms.active", "{alarm}",
            "Current active alarms by severity.");

    public static Counter<long> StreamFrames { get; } =
        Meter.CreateCounter<long>("upperhost.stream.frames", "{frame}",
            "Stream frames observed.");

    public static Counter<long> StreamDroppedFrames { get; } =
        Meter.CreateCounter<long>("upperhost.stream.dropped_frames", "{frame}",
            "Stream frames dropped by bounded pipelines.");

    public static Activity? StartActivity(
        string name,
        ActivityKind kind = ActivityKind.Internal,
        UpperHostTelemetryContext? context = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var activity = ActivitySource.StartActivity(name, kind);
        if (activity is not null && context is not null)
            Apply(activity, context);
        return activity;
    }

    public static TagList CreateMetricTags(UpperHostMetricContext? context)
    {
        var tags = new TagList();
        if (context is null)
            return tags;

        Add(ref tags, "upperhost.protocol", context.Protocol);
        Add(ref tags, "upperhost.transport", context.Transport);
        Add(ref tags, "upperhost.operation", context.Operation);
        Add(ref tags, "upperhost.result", context.Outcome);
        Add(ref tags, "error.type", context.ErrorType);
        Add(ref tags, "upperhost.alarm.severity", context.AlarmSeverity);
        return tags;
    }

    private static void Apply(Activity activity, UpperHostTelemetryContext context)
    {
        Set(activity, "upperhost.device.id", context.DeviceId);
        Set(activity, "upperhost.connection.id", context.ConnectionId);
        Set(activity, "upperhost.session.id", context.SessionId);
        Set(activity, "upperhost.command.id", context.CommandId);
        Set(activity, "upperhost.protocol", context.Protocol);
        Set(activity, "upperhost.transport", context.Transport);
        Set(activity, "upperhost.operation", context.Operation);
        Set(activity, "upperhost.result", context.Result);
        Set(activity, "upperhost.error.code", context.ErrorCode);
    }

    private static void Add(ref TagList tags, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            tags.Add(key, value);
    }

    private static void Set(Activity activity, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            activity.SetTag(key, value);
    }
}
