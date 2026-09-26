using Microsoft.Extensions.Logging;

namespace OpenDeviceStudio.Observability;

public sealed record OpenDeviceStudioLogContext(
    string? DeviceId = null,
    string? ConnectionId = null,
    string? SessionId = null,
    string? CommandId = null,
    string? Protocol = null,
    string? Transport = null,
    string? Operation = null);

public static class OpenDeviceStudioLoggerExtensions
{
    public static IDisposable BeginOpenDeviceStudioScope(
        this ILogger logger,
        OpenDeviceStudioLogContext context)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(context);

        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["DeviceId"] = context.DeviceId,
            ["ConnectionId"] = context.ConnectionId,
            ["SessionId"] = context.SessionId,
            ["CommandId"] = context.CommandId,
            ["Protocol"] = context.Protocol,
            ["Transport"] = context.Transport,
            ["Operation"] = context.Operation
        };

        foreach (var key in values.Where(pair => pair.Value is null).Select(pair => pair.Key).ToArray())
            values.Remove(key);

        return logger.BeginScope(values) ?? EmptyScope.Instance;
    }

    private sealed class EmptyScope : IDisposable
    {
        public static EmptyScope Instance { get; } = new();
        public void Dispose() { }
    }
}
