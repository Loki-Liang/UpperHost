namespace UpperHost.Abstractions.Devices;

public interface IDeviceRegistry
{
    IReadOnlyCollection<IDevice> Devices { get; }
    bool Register(IDevice device);
    bool Unregister(string deviceId);
    bool TryGet(string deviceId, out IDevice? device);
}
