using Serilog.Sinks.Async;
using UpperHost.Abstractions.Diagnostics;

namespace UpperHost.Observability;

public sealed class AsyncLogBufferMonitor : IAsyncLogEventSinkMonitor, IHealthProbe
{
    private IAsyncLogEventSinkInspector? _inspector;

    public string Name => "upperhost.logging.async_buffer";

    public void StartMonitoring(IAsyncLogEventSinkInspector inspector)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        Volatile.Write(ref _inspector, inspector);
    }

    public void StopMonitoring(IAsyncLogEventSinkInspector inspector)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        Interlocked.CompareExchange(ref _inspector, null, inspector);
    }

    public Task<HealthReport> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var inspector = Volatile.Read(ref _inspector);
        if (inspector is null)
        {
            return Task.FromResult(new HealthReport(
                Name,
                HealthStatus.Healthy,
                "Async file logging is not active."));
        }

        try
        {
            var bufferSize = inspector.BufferSize;
            var count = inspector.Count;
            var dropped = inspector.DroppedMessagesCount;
            var usageRatio = bufferSize == 0 ? 0d : (double)count / bufferSize;

            var status = dropped > 0 || usageRatio >= 0.8
                ? HealthStatus.Degraded
                : HealthStatus.Healthy;
            var description = dropped > 0
                ? $"{dropped} log event(s) have been dropped because the async buffer was full."
                : usageRatio >= 0.8
                    ? "Async log buffer utilization is at or above 80%."
                    : "Async log buffer is healthy.";

            IReadOnlyDictionary<string, object?> data = new Dictionary<string, object?>
            {
                ["BufferSize"] = bufferSize,
                ["Count"] = count,
                ["UsageRatio"] = usageRatio,
                ["DroppedMessages"] = dropped
            };

            return Task.FromResult(new HealthReport(Name, status, description, data));
        }
        catch (ObjectDisposedException)
        {
            return Task.FromResult(new HealthReport(
                Name,
                HealthStatus.Healthy,
                "Async file logging has been disposed."));
        }
    }
}
