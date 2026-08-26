using Microsoft.Extensions.Hosting;
using UpperHost.Abstractions.Devices;

namespace UpperHost.Hosting.Devices;

internal sealed class DeviceRegistrationHostedService : IHostedService
{
    private readonly IDeviceRegistry _registry;
    private readonly IReadOnlyCollection<IDevice> _devices;

    public DeviceRegistrationHostedService(IDeviceRegistry registry, IEnumerable<IDevice> devices)
    {
        _registry = registry;
        _devices = devices.ToArray();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var device in _devices)
            _registry.Register(device);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var device in _devices)
            _registry.Unregister(device.Descriptor.Id);

        return Task.CompletedTask;
    }
}
