using System.Globalization;
using System.Windows;
using UpperHost.Abstractions.Devices;
using UpperHost.Control.Commands;
using UpperHost.Control.Scheduling;
using UpperHost.Sample.DeviceControl.Device;
using UpperHost.Sample.DeviceControl.Domain;

namespace UpperHost.Sample.DeviceControl;

public partial class MainWindow : Window
{
    private readonly TemperatureControllerDevice _device;
    private readonly IDeviceRegistry _registry;
    private static readonly CommandResourceKey DeviceResource =
        new("device", TemperatureControllerDevice.DeviceId);
    private static readonly CommandSafetyMetadata ReadSafety =
        new(ReadOnly: true, Idempotent: true, Motion: false, Hazardous: false, RetryAllowed: true);

    private readonly IDevicePackageCatalog _catalog;
    private readonly BoundedCommandDispatcher<TemperatureControllerCommand, TemperatureControllerResponse> _dispatcher;

    public MainWindow(
        TemperatureControllerDevice device,
        IDeviceRegistry registry,
        IDevicePackageCatalog catalog,
        BoundedCommandDispatcher<TemperatureControllerCommand, TemperatureControllerResponse> dispatcher)
    {
        InitializeComponent();
        _device = device;
        _registry = registry;
        _catalog = catalog;
        _dispatcher = dispatcher;
        RefreshDeviceState();
    }

    private async void Connect_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(async () =>
        {
            await _device.ConnectAsync();
            await ReadAndRenderAsync();
        });

    private async void Disconnect_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(async () =>
        {
            await _device.DisconnectAsync();
            MessageText.Text = "Disconnected.";
        });

    private async void ReadStatus_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(ReadAndRenderAsync);

    private async void SetTarget_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(async () =>
        {
            if (!double.TryParse(TargetTextBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var target))
                throw new InvalidOperationException("Enter a numeric target temperature.");

            await _device.SetParameterAsync("targetCelsius", target);
            await ReadAndRenderAsync();
            MessageText.Text = "Target written and verified by readback.";
        });

    private async void Start_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(async () => Render(await DispatchAsync(new SetRunningCommand(true))));

    private async void Stop_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(async () => Render(await DispatchAsync(new SetRunningCommand(false))));

    private async Task ReadAndRenderAsync() =>
        Render(await DispatchAsync(new ReadStatusCommand()));

    private async Task<TemperatureControllerResponse> DispatchAsync(TemperatureControllerCommand command)
    {
        var readOnly = command is ReadStatusCommand;
        var result = await _dispatcher.EnqueueAsync(
            command,
            new CommandDispatchOptions(
                Resources: [DeviceResource],
                Safety: readOnly ? ReadSafety : CommandSafetyMetadata.MutatingNonIdempotent));

        if (!result.IsSuccess || result.Value is null)
            throw new InvalidOperationException(result.Message ?? result.Code ?? "Command dispatch failed.");

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
        DeviceStateText.Text =
            $"{_device.State} / registry={(registered ? "registered" : "missing")} / package={packageState}";
    }
}
