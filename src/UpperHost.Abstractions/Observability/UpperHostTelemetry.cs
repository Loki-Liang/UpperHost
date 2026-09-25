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

public static class UpperHostTelemetry
{
    public const string InstrumentationName = "UpperHost";
    public const string InstrumentationVersion = "0.1.0";

    public static ActivitySource ActivitySource { get; } =
        new(InstrumentationName, InstrumentationVersion);

    public static Meter Meter { get; } =
        new(InstrumentationName, InstrumentationVersion);

    public static Counter<long> CommandExecutions { get; } =
        Meter.CreateCounter<long>("upperhost.command.executions", unit: "{command}");

    public static Counter<long> CommandFailures { get; } =
        Meter.CreateCounter<long>("upperhost.command.failures", unit: "{command}");

    public static Histogram<double> CommandDurationMilliseconds { get; } =
        Meter.CreateHistogram<double>("upperhost.command.duration", unit: "ms");

    public static Counter<long> TransportOperations { get; } =
        Meter.CreateCounter<long>("upperhost.transport.operations", unit: "{operation}");

    public static Counter<long> TransportFailures { get; } =
        Meter.CreateCounter<long>("upperhost.transport.failures", unit: "{operation}");

    public static Counter<long> TransportBytes { get; } =
        Meter.CreateCounter<long>("upperhost.transport.bytes", unit: "By");

    public static Counter<long> ReconnectAttempts { get; } =
        Meter.CreateCounter<long>("upperhost.transport.reconnect.attempts", unit: "{attempt}");

    public static UpDownCounter<long> ActiveAlarms { get; } =
        Meter.CreateUpDownCounter<long>("upperhost.alarms.active", unit: "{alarm}");

    public static Counter<long> StreamFrames { get; } =
        Meter.CreateCounter<long>("upperhost.stream.frames", unit: "{frame}");

    public static Counter<long> StreamDroppedFrames { get; } =
        Meter.CreateCounter<long>("upperhost.stream.dropped_frames", unit: "{frame}");

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

    public static TagList CreateTags(UpperHostTelemetryContext? context)
    {
        var tags = new TagList();
        if (context is null)
            return tags;

        Add(tags, "upperhost.device.id", context.DeviceId);
        Add(tags, "upperhost.connection.id", context.ConnectionId);
        Add(tags, "upperhost.session.id", context.SessionId);
        Add(tags, "upperhost.command.id", context.CommandId);
        Add(tags, "upperhost.protocol", context.Protocol);
        Add(tags, "upperhost.transport", context.Transport);
        Add(tags, "upperhost.operation", context.Operation);
        Add(tags, "upperhost.result", context.Result);
        Add(tags, "upperhost.error.code", context.ErrorCode);
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

    private static void Add(TagList tags, string key, string? value)
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
