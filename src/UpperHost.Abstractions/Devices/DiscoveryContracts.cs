using UpperHost.Abstractions.Transports;

namespace UpperHost.Abstractions.Devices;

public sealed record DiscoveredDevice(
    DeviceDescriptor Descriptor,
    TransportEndpoint? Endpoint = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public interface IDeviceDiscoverer
{
    string Name { get; }
    IAsyncEnumerable<DiscoveredDevice> DiscoverAsync(CancellationToken cancellationToken = default);
}

public interface IDeviceDiscoveryService
{
    IAsyncEnumerable<DiscoveredDevice> DiscoverAllAsync(CancellationToken cancellationToken = default);
}
