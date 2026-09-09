using System.Globalization;
using System.Windows;
using UpperHost.Abstractions.Devices;
using UpperHost.Sample.DeviceControl.Device;
using UpperHost.Sample.DeviceControl.Domain;

namespace UpperHost.Sample.DeviceControl;

public partial class MainWindow : Window
{
    private readonly TemperatureControllerDevice _device;
    private readonly IDeviceRegistry _registry;

    public MainWindow(TemperatureControllerDevice device, IDeviceRegistry registry)
    {
        InitializeComponent();
        _device = device;
        _registry = registry;
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
        await RunAsync(async () => Render(await _device.ExecuteAsync(new SetRunningCommand(true))));

    private async void Stop_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(async () => Render(await _device.ExecuteAsync(new SetRunningCommand(false))));

    private async Task ReadAndRenderAsync() =>
        Render(await _device.ExecuteAsync(new ReadStatusCommand()));

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
        DeviceStateText.Text = $"{_device.State} / registry={(registered ? "registered" : "missing")}";
    }
}
