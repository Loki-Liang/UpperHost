using UpperHost.Abstractions.Diagnostics;
using UpperHost.Abstractions.Transports;

namespace UpperHost.Observability;

public sealed class TransportHealthProbe : IHealthProbe
{
    private readonly IReadOnlyCollection<ITransport> _transports;

    public TransportHealthProbe(IEnumerable<ITransport> transports) =>
        _transports = transports?.ToArray() ?? throw new ArgumentNullException(nameof(transports));

    public string Name => "upperhost.transports";

    public Task<HealthReport> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_transports.Count == 0)
        {
            return Task.FromResult(new HealthReport(
                Name,
                HealthStatus.Healthy,
                "No transports are registered.",
                new Dictionary<string, object?> { ["Count"] = 0 }));
        }

        var faulted = _transports.Count(transport => transport.State == TransportState.Faulted);
        var opening = _transports.Count(transport => transport.State == TransportState.Opening);
        var open = _transports.Count(transport => transport.State == TransportState.Open);
        var closed = _transports.Count - faulted - opening - open;

        var status = faulted > 0
            ? HealthStatus.Unhealthy
            : opening > 0
                ? HealthStatus.Degraded
                : HealthStatus.Healthy;

        var description = faulted > 0
            ? $"{faulted} transport(s) are faulted."
            : opening > 0
                ? $"{opening} transport(s) are opening."
                : "Transport states are healthy.";

        IReadOnlyDictionary<string, object?> data = new Dictionary<string, object?>
        {
            ["Count"] = _transports.Count,
            ["Open"] = open,
            ["Opening"] = opening,
            ["Closed"] = closed,
            ["Faulted"] = faulted
        };

        return Task.FromResult(new HealthReport(Name, status, description, data));
    }
}
