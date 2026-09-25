using Microsoft.Extensions.Hosting;
using UpperHost.Abstractions.Devices;

namespace UpperHost.Hosting.Devices;

internal sealed class DevicePackageRegistrationHostedService : IHostedService
{
    private readonly IDevicePackageCatalog _catalog;
    private readonly IReadOnlyCollection<DevicePackageDescriptor> _descriptors;

    public DevicePackageRegistrationHostedService(
        IDevicePackageCatalog catalog,
        IEnumerable<DevicePackageDescriptor> descriptors)
    {
        _catalog = catalog;
        _descriptors = descriptors.ToArray();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var descriptor in _descriptors
                     .OrderBy(item => item.PackageId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.PackageVersion))
        {
            _catalog.Register(descriptor);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
