using System.Globalization;
using OpenDeviceStudio.Abstractions.Devices;
using OpenDeviceStudio.Abstractions.Transports;
using OpenDeviceStudio.Protocols;
using OpenDeviceStudio.Sample.DeviceControl.Domain;

namespace OpenDeviceStudio.Sample.DeviceControl.Device;

public sealed class TemperatureControllerDevice :
    IDevice,
    IConnectable,
    ICommandable<TemperatureControllerCommand, TemperatureControllerResponse>,
    IDirectParameterProvider,
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
        "OpenDeviceStudio",
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
        return CreateDescriptors(status);
    }

    public async Task<DeviceParameterDescriptor> GetParameterAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var status = await ExecuteAsync(new ReadStatusCommand(), cancellationToken).ConfigureAwait(false);
        return CreateDescriptors(status).FirstOrDefault(descriptor =>
                   descriptor.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
               ?? throw new KeyNotFoundException($"Unknown parameter '{key}'.");
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

        await ExecuteAsync(
            new SetTargetTemperatureCommand(target),
            cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<DeviceParameterDescriptor> CreateDescriptors(
        TemperatureControllerResponse status) =>
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
