namespace UpperHost.Abstractions.Devices;

public enum DeviceState
{
    Offline,
    Connecting,
    Online,
    Busy,
    Faulted
}

public sealed record DeviceDescriptor(
    string Id,
    string DisplayName,
    string? Vendor = null,
    string? Model = null,
    string? SerialNumber = null);

public interface IDevice
{
    DeviceDescriptor Descriptor { get; }
    DeviceState State { get; }
    IReadOnlyCollection<string> Capabilities { get; }
}

public interface IConnectable
{
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}

public interface IConfigurable<in TConfiguration>
{
    Task ConfigureAsync(TConfiguration configuration, CancellationToken cancellationToken = default);
}

public interface ICommandable<in TCommand, TResult>
{
    Task<TResult> ExecuteAsync(TCommand command, CancellationToken cancellationToken = default);
}

public interface ICalibratable<TRequest, TResult>
{
    Task<TResult> CalibrateAsync(TRequest request, CancellationToken cancellationToken = default);
}

public interface IDiagnosable<TResult>
{
    Task<TResult> DiagnoseAsync(CancellationToken cancellationToken = default);
}

public interface IResettable
{
    Task ResetAsync(CancellationToken cancellationToken = default);
}
