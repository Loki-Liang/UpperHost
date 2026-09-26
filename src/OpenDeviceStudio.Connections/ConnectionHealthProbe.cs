using OpenDeviceStudio.Abstractions.Connections;
using OpenDeviceStudio.Abstractions.Diagnostics;

namespace OpenDeviceStudio.Connections;

public sealed class ConnectionHealthProbe : IHealthProbe
{
    private readonly IConnectionManager _manager;

    public ConnectionHealthProbe(IConnectionManager manager) =>
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));

    public string Name => "opendevicestudio.connections";

    public Task<HealthReport> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var connections = _manager.Connections;
        if (connections.Count == 0)
        {
            return Task.FromResult(new HealthReport(
                Name,
                HealthStatus.Healthy,
                "No managed connections are registered.",
                new Dictionary<string, object?> { ["Count"] = 0 }));
        }

        var faulted = connections.Count(connection => connection.State == ConnectionState.Faulted);
        var transitional = connections.Count(connection =>
            connection.State is ConnectionState.Opening or
                ConnectionState.Reconnecting or
                ConnectionState.Closing);
        var invariantFailures = connections.Count(connection =>
            connection.LeaseCount > 0 && connection.State != ConnectionState.Open);

        var status = faulted > 0 || invariantFailures > 0
            ? HealthStatus.Unhealthy
            : transitional > 0
                ? HealthStatus.Degraded
                : HealthStatus.Healthy;

        IReadOnlyDictionary<string, object?> data = new Dictionary<string, object?>
        {
            ["Count"] = connections.Count,
            ["Open"] = connections.Count(connection => connection.State == ConnectionState.Open),
            ["Closed"] = connections.Count(connection => connection.State == ConnectionState.Closed),
            ["Faulted"] = faulted,
            ["Transitional"] = transitional,
            ["Leases"] = connections.Sum(connection => connection.LeaseCount)
        };

        var description = status switch
        {
            HealthStatus.Unhealthy => "One or more managed connections are faulted or violate lease/state invariants.",
            HealthStatus.Degraded => "One or more managed connections are transitioning.",
            _ => "Managed connections are healthy."
        };

        return Task.FromResult(new HealthReport(Name, status, description, data));
    }
}
