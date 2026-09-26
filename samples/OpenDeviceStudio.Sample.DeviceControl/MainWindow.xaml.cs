using System.Globalization;
using System.Windows;
using OpenDeviceStudio.Abstractions.Devices;
using OpenDeviceStudio.Control.Commands;
using OpenDeviceStudio.Control.Parameters;
using OpenDeviceStudio.Control.Scheduling;
using OpenDeviceStudio.Control.State;
using OpenDeviceStudio.Sample.DeviceControl.Device;
using OpenDeviceStudio.Sample.DeviceControl.Domain;

namespace OpenDeviceStudio.Sample.DeviceControl;

public partial class MainWindow : Window
{
    private readonly TemperatureControllerDevice _device;
    private readonly IDeviceRegistry _registry;
    private static readonly CommandResourceKey DeviceResource =
        new("device", TemperatureControllerDevice.DeviceId);
    private static readonly CommandSafetyMetadata ReadSafety =
        new(ReadOnly: true, Idempotent: true, Motion: false, Hazardous: false, RetryAllowed: true);

    private static readonly DeviceStatePartitionKey StatusPartition = new("status");
    private static readonly ParameterContract<double> TargetContract =
        new(
            "targetCelsius",
            ReadbackComparer: ParameterComparers.Absolute(0.01),
            Range: new ParameterRange<double>(5, 95),
            StaleAfter: TimeSpan.FromSeconds(3));

    private readonly IDevicePackageCatalog _catalog;
    private readonly BoundedCommandDispatcher<TemperatureControllerCommand, TemperatureControllerResponse> _dispatcher;
    private readonly DeviceControlStateRegistry _controlState;
    private readonly DeviceSnapshotStore<TemperatureControllerResponse> _statusSnapshots;
    private readonly TypedParameterRuntime<double> _targetParameter;
    private readonly TimeProvider _timeProvider;

    public MainWindow(
        TemperatureControllerDevice device,
        IDeviceRegistry registry,
        IDevicePackageCatalog catalog,
        BoundedCommandDispatcher<TemperatureControllerCommand, TemperatureControllerResponse> dispatcher,
        DeviceControlStateRegistry controlState,
        DeviceSnapshotStore<TemperatureControllerResponse> statusSnapshots,
        TypedParameterRuntime<double> targetParameter,
        TimeProvider timeProvider)
    {
        InitializeComponent();
        _device = device;
        _registry = registry;
        _catalog = catalog;
        _dispatcher = dispatcher;
        _controlState = controlState;
        _statusSnapshots = statusSnapshots;
        _targetParameter = targetParameter;
        _timeProvider = timeProvider;
        RefreshDeviceState();
    }

    private async void Connect_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(ConnectAndRehydrateAsync);

    private async void Disconnect_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(async () =>
        {
            await _device.DisconnectAsync();
            _controlState.MarkDisconnected(TemperatureControllerDevice.DeviceId);
            MessageText.Text = "Disconnected.";
        });

    private async void ReadStatus_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(ReadAndRenderAsync);

    private async void SetTarget_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(async () =>
        {
            if (!double.TryParse(TargetTextBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var target))
                throw new InvalidOperationException("Enter a numeric target temperature.");

            var result = await _targetParameter.WriteAsync(TargetContract, target);
            if (!result.Verified)
                throw new InvalidOperationException(
                    result.Message ?? result.Code ?? "Target write could not be verified.");

            TargetText.Text = $"Target: {result.ObservedValue:0.0} °C";
            MessageText.Text = "Target written and verified by authoritative readback.";
        });

    private async void Start_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(async () => Render(await DispatchAsync(new SetRunningCommand(true))));

    private async void Stop_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(async () => Render(await DispatchAsync(new SetRunningCommand(false))));

    private Task ReadAndRenderAsync()
    {
        var snapshot = _statusSnapshots.Get(
            TemperatureControllerDevice.DeviceId,
            StatusPartition);

        if (snapshot is null)
            throw new InvalidOperationException("No authoritative status snapshot is available yet.");
        if (snapshot.Quality != DeviceSnapshotQuality.Good)
            throw new InvalidOperationException(
                $"Status snapshot is {snapshot.Quality}: {snapshot.QualityReason ?? "no reason"}.");

        Render(snapshot.Value);
        return Task.CompletedTask;
    }

    private async Task ConnectAndRehydrateAsync()
    {
        var rehydrate = _controlState.BeginRehydrate(
            TemperatureControllerDevice.DeviceId,
            ["status", "targetCelsius"]);

        try
        {
            await _device.ConnectAsync();

            await _targetParameter.ReadAsync(TargetContract);
            _controlState.MarkRequirementSatisfied(
                TemperatureControllerDevice.DeviceId,
                rehydrate.ConnectionEpoch,
                "targetCelsius");

            var status = await WaitForFreshStatusAsync(rehydrate.ConnectionEpoch);
            _controlState.MarkRequirementSatisfied(
                TemperatureControllerDevice.DeviceId,
                rehydrate.ConnectionEpoch,
                "status");

            Render(status.Value);
            MessageText.Text = "Connected and rehydrated.";
        }
        catch (Exception ex)
        {
            _controlState.MarkRequirementFailed(
                TemperatureControllerDevice.DeviceId,
                rehydrate.ConnectionEpoch,
                "status",
                ex.GetType().Name);
            throw;
        }
    }

    private async Task<DeviceSnapshot<TemperatureControllerResponse>> WaitForFreshStatusAsync(
        long connectionEpoch)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        await foreach (var snapshot in _statusSnapshots.Subscribe(
                           TemperatureControllerDevice.DeviceId,
                           StatusPartition,
                           timeout.Token))
        {
            if (snapshot.ConnectionEpoch == connectionEpoch &&
                snapshot.Quality == DeviceSnapshotQuality.Good)
                return snapshot;
        }

        throw new TimeoutException("Timed out waiting for a fresh status snapshot.");
    }

    private async Task<TemperatureControllerResponse> DispatchAsync(TemperatureControllerCommand command)
    {
        var readOnly = command is ReadStatusCommand;
        var result = await _dispatcher.EnqueueAsync(
            command,
            new CommandDispatchOptions(
                Resources: [DeviceResource],
                Safety: readOnly ? ReadSafety : CommandSafetyMetadata.MutatingNonIdempotent,
                ConnectionEpoch: _controlState.GetCurrentEpoch(
                    TemperatureControllerDevice.DeviceId)));

        if (!result.IsSuccess || result.Value is null)
            throw new InvalidOperationException(result.Message ?? result.Code ?? "Command dispatch failed.");

        var epoch = _controlState.GetCurrentEpoch(TemperatureControllerDevice.DeviceId);
        _statusSnapshots.Apply(new DeviceObservation<TemperatureControllerResponse>(
            TemperatureControllerDevice.DeviceId,
            StatusPartition,
            epoch,
            DeviceObservationSource.Command,
            _timeProvider.GetTimestamp(),
            result.Value,
            DeviceSnapshotQuality.Good,
            ObservedAtUtc: _timeProvider.GetUtcNow(),
            StaleAfter: TimeSpan.FromSeconds(3)));

        return result.Value;
    }

    private void Render(TemperatureControllerResponse response)
    {
        ActualText.Text = $"Actual: {response.ActualCelsius:0.0} °C";
        TargetText.Text = $"Target: {response.TargetCelsius:0.0} °C";
        RunningText.Text = $"Running: {response.Running}";
        MessageText.Text = response.Message;
    }

    private async Task RunAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            MessageText.Text = ex.Message;
        }
        finally
        {
            RefreshDeviceState();
        }
    }

    private void RefreshDeviceState()
    {
        var registered = _registry.TryGet(TemperatureControllerDevice.DeviceId, out _);
        var packageRegistered = _catalog.TryGet(TemperatureControllerPackage.PackageId, out var package);
        var packageState = packageRegistered
            ? $"{package!.PackageId}@{package.PackageVersion}"
            : "missing";
        var readiness = _controlState.GetReadiness(TemperatureControllerDevice.DeviceId);
        DeviceStateText.Text =
            $"{_device.State} / control={readiness.State}@{readiness.ConnectionEpoch} / " +
            $"registry={(registered ? "registered" : "missing")} / package={packageState}";
    }
}
