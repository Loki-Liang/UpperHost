using System.Collections.Concurrent;
using UpperHost.Abstractions.Devices;

namespace UpperHost.Hosting.Devices;

internal sealed class DeviceRegistry : IDeviceRegistry
{
    private readonly ConcurrentDictionary<string, IDevice> _devices = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<IDevice> Devices => _devices.Values.ToArray();

    public bool Register(IDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return _devices.TryAdd(device.Descriptor.Id, device);
    }

    public bool Unregister(string deviceId) => _devices.TryRemove(deviceId, out _);

    public bool TryGet(string deviceId, out IDevice? device) => _devices.TryGetValue(deviceId, out device);
}
