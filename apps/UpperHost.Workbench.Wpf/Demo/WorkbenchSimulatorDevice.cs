using UpperHost.Abstractions.Devices;

namespace UpperHost.Workbench.Wpf.Demo;

public sealed class WorkbenchSimulatorDevice :
    IDevice,
    IConnectable,
    IParameterProvider,
    ICommandDescriptorProvider
{
    private double _targetCelsius = 30;

    public DeviceDescriptor Descriptor { get; } = new(
        "workbench-simulator-1",
        "Workbench Simulator",
        "UpperHost",
        "Deterministic Temperature Controller",
        "SIM-001");

    public DeviceState State { get; private set; } = DeviceState.Offline;

    public IReadOnlyCollection<string> Capabilities { get; } =
        ["connect", "commands", "parameters", "simulator"];

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        State = DeviceState.Online;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        State = DeviceState.Offline;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DeviceParameterDescriptor>> GetParametersAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<DeviceParameterDescriptor> parameters =
        [
            new(
                "targetCelsius",
                "Target temperature",
                DeviceParameterKind.Decimal,
                _targetCelsius,
                "°C",
                0,
                100)
        ];
        return Task.FromResult(parameters);
    }

    public Task SetParameterAsync(
        string key,
        object? value,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(key, "targetCelsius", StringComparison.OrdinalIgnoreCase))
            throw new KeyNotFoundException($"Unknown simulator parameter '{key}'.");

        if (value is null || !double.TryParse(value.ToString(), out var parsed) || parsed is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(value), value, "Target temperature must be between 0 and 100 °C.");

        _targetCelsius = parsed;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DeviceCommandDescriptor>> GetCommandsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<DeviceCommandDescriptor> commands =
        [
            new("connect", "Connect", "Connect the deterministic Workbench simulator."),
            new("disconnect", "Disconnect", "Disconnect the deterministic Workbench simulator."),
            new("refresh", "Refresh", "Refresh current simulator metadata.")
        ];
        return Task.FromResult(commands);
    }
}
