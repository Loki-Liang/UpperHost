using System.Globalization;
using UpperHost.Abstractions.Devices;
using UpperHost.Abstractions.Transports;
using UpperHost.Protocols;
using UpperHost.Sample.DeviceControl.Domain;

namespace UpperHost.Sample.DeviceControl.Device;

public sealed class TemperatureControllerDevice :
    IDevice,
    IConnectable,
    ICommandable<TemperatureControllerCommand, TemperatureControllerResponse>,
    IParameterProvider,
    ICommandDescriptorProvider
{
    public const string DeviceId = "temperature-controller-1";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(2);
    private readonly ITransport _transport;
    private readonly RequestResponseClient<TemperatureControllerCommand, TemperatureControllerResponse> _client;

    public TemperatureControllerDevice(
        ITransport transport,
        RequestResponseClient<TemperatureControllerCommand, TemperatureControllerResponse> client)
    {
        _transport = transport;
        _client = client;
    }

    public DeviceDescriptor Descriptor { get; } = new(
        DeviceId,
        "Simulated Temperature Controller",
        "UpperHost",
        "TC-SIM-1",
        "SIM-0001");

    public DeviceState State { get; private set; } = DeviceState.Offline;

    public IReadOnlyCollection<string> Capabilities { get; } =
        ["connect", "command", "parameters", "readback"];

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (State is DeviceState.Online or DeviceState.Busy)
            return;

        State = DeviceState.Connecting;
        try
        {
            await _transport.OpenAsync(cancellationToken).ConfigureAwait(false);
            State = DeviceState.Online;
        }
        catch
        {
            State = DeviceState.Faulted;
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _transport.CloseAsync(cancellationToken).ConfigureAwait(false);
        State = DeviceState.Offline;
    }

    public async Task<TemperatureControllerResponse> ExecuteAsync(
        TemperatureControllerCommand command,
        CancellationToken cancellationToken = default)
    {
        if (State != DeviceState.Online)
            throw new InvalidOperationException($"Device must be Online before executing commands. Current state: {State}.");

        State = DeviceState.Busy;
        try
        {
            var response = await _client.ExecuteAsync(command, CommandTimeout, cancellationToken).ConfigureAwait(false);
            if (!response.Success)
                throw new InvalidOperationException($"Device rejected command: {response.Message}");
            return response;
        }
        catch
        {
            State = DeviceState.Faulted;
            throw;
        }
        finally
        {
            if (State == DeviceState.Busy)
                State = DeviceState.Online;
        }
    }

    public async Task<IReadOnlyList<DeviceParameterDescriptor>> GetParametersAsync(
        CancellationToken cancellationToken = default)
    {
        var status = await ExecuteAsync(new ReadStatusCommand(), cancellationToken).ConfigureAwait(false);
        return
        [
            new DeviceParameterDescriptor(
                "targetCelsius",
                "Target temperature",
                DeviceParameterKind.Decimal,
                status.TargetCelsius,
                "°C",
                5,
                95),
            new DeviceParameterDescriptor(
                "actualCelsius",
                "Actual temperature",
                DeviceParameterKind.Decimal,
                status.ActualCelsius,
                "°C",
                IsReadOnly: true),
            new DeviceParameterDescriptor(
                "running",
                "Running",
                DeviceParameterKind.Boolean,
                status.Running,
                IsReadOnly: true)
        ];
    }

    public async Task SetParameterAsync(
        string key,
        object? value,
        CancellationToken cancellationToken = default)
    {
        if (!key.Equals("targetCelsius", StringComparison.OrdinalIgnoreCase))
            throw new KeyNotFoundException($"Unknown or read-only parameter '{key}'.");

        var target = value switch
        {
            double number => number,
            float number => number,
            decimal number => (double)number,
            string text when double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var number) => number,
            string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
            _ => throw new ArgumentException("Target temperature must be numeric.", nameof(value))
        };

        var write = await ExecuteAsync(new SetTargetTemperatureCommand(target), cancellationToken).ConfigureAwait(false);
        var readback = await ExecuteAsync(new ReadStatusCommand(), cancellationToken).ConfigureAwait(false);

        if (Math.Abs(readback.TargetCelsius - target) > 0.01)
            throw new InvalidOperationException(
                $"Readback mismatch. Requested {target:0.00} °C but device reports {readback.TargetCelsius:0.00} °C.");

        if (Math.Abs(write.TargetCelsius - readback.TargetCelsius) > 0.01)
            throw new InvalidOperationException("Write acknowledgement and readback disagree.");
    }

    public Task<IReadOnlyList<DeviceCommandDescriptor>> GetCommandsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<DeviceCommandDescriptor> commands =
        [
            new("readStatus", "Read status"),
            new("start", "Start temperature control"),
            new("stop", "Stop temperature control")
        ];
        return Task.FromResult(commands);
    }
}
