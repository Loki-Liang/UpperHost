using UpperHost.Abstractions.Devices;

namespace UpperHost.Hosting.Devices;

internal sealed class DeviceManager : IDeviceManager
{
    private readonly IDeviceRegistry _registry;

    public DeviceManager(IDeviceRegistry registry) => _registry = registry;

    public Task ConnectAsync(string deviceId, CancellationToken cancellationToken = default) =>
        RequireCapability<IConnectable>(deviceId, "connect").ConnectAsync(cancellationToken);

    public Task DisconnectAsync(string deviceId, CancellationToken cancellationToken = default) =>
        RequireCapability<IConnectable>(deviceId, "disconnect").DisconnectAsync(cancellationToken);

    public Task ResetAsync(string deviceId, CancellationToken cancellationToken = default) =>
        RequireCapability<IResettable>(deviceId, "reset").ResetAsync(cancellationToken);

    private TCapability RequireCapability<TCapability>(string deviceId, string operation)
        where TCapability : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        if (!_registry.TryGet(deviceId, out var device) || device is null)
            throw new KeyNotFoundException($"UpperHost device '{deviceId}' is not registered.");

        return device as TCapability
            ?? throw new NotSupportedException(
                $"UpperHost device '{deviceId}' does not support capability '{typeof(TCapability).Name}' required to {operation}.");
    }
}
