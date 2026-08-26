namespace UpperHost.Abstractions.Diagnostics;

public enum HealthStatus
{
    Healthy,
    Degraded,
    Unhealthy
}

public sealed record HealthReport(
    string Name,
    HealthStatus Status,
    string? Description = null,
    IReadOnlyDictionary<string, object?>? Data = null);

public interface IHealthProbe
{
    string Name { get; }
    Task<HealthReport> CheckAsync(CancellationToken cancellationToken = default);
}
