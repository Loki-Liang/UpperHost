using UpperHost.Abstractions.Diagnostics;

namespace UpperHost.Diagnostics;

public sealed class HealthService
{
    private readonly IReadOnlyCollection<IHealthProbe> _probes;

    public HealthService(IEnumerable<IHealthProbe> probes) => _probes = probes.ToArray();

    public async Task<IReadOnlyList<HealthReport>> CheckAllAsync(CancellationToken cancellationToken = default)
    {
        var tasks = _probes.Select(probe => CheckOneAsync(probe, cancellationToken));
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task<HealthReport> CheckOneAsync(IHealthProbe probe, CancellationToken cancellationToken)
    {
        try
        {
            return await probe.CheckAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new HealthReport(probe.Name, HealthStatus.Unhealthy, ex.Message);
        }
    }
}
