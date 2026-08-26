namespace UpperHost.Abstractions.Devices;

public interface IDeviceManager
{
    Task ConnectAsync(string deviceId, CancellationToken cancellationToken = default);
    Task DisconnectAsync(string deviceId, CancellationToken cancellationToken = default);
    Task ResetAsync(string deviceId, CancellationToken cancellationToken = default);
}
