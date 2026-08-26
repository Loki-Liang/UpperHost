using System.Runtime.CompilerServices;
using UpperHost.Abstractions.Devices;

namespace UpperHost.Hosting.Devices;

internal sealed class DeviceDiscoveryService : IDeviceDiscoveryService
{
    private readonly IReadOnlyCollection<IDeviceDiscoverer> _discoverers;

    public DeviceDiscoveryService(IEnumerable<IDeviceDiscoverer> discoverers) =>
        _discoverers = discoverers.ToArray();

    public async IAsyncEnumerable<DiscoveredDevice> DiscoverAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var discoverer in _discoverers)
        {
            await foreach (var device in discoverer.DiscoverAsync(cancellationToken).ConfigureAwait(false))
                yield return device;
        }
    }
}
