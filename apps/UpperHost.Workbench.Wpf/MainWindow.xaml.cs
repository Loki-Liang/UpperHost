using System.Collections.ObjectModel;
using System.Windows;
using UpperHost.Abstractions.Devices;
using UpperHost.Abstractions.Diagnostics;
using UpperHost.Diagnostics;
using UpperHost.Presentation.Wpf.Mvvm;
using UpperHost.Workbench.Application;
using UpperHost.Workbench.Wpf.Demo;

namespace UpperHost.Workbench.Wpf;

public partial class MainWindow : Window
{
    private readonly WorkbenchShellViewModel _shell;
    private readonly WorkbenchSimulatorDevice _simulator;
    private readonly IAlarmService _alarmService;
    private readonly HealthService _healthService;
    private readonly ParameterEditorViewModel _parameterEditor;

    public MainWindow(
        WorkbenchShellViewModel shell,
        WorkbenchSimulatorDevice simulator,
        IAlarmService alarmService,
        HealthService healthService)
    {
        InitializeComponent();
        _shell = shell;
        _simulator = simulator;
        _alarmService = alarmService;
        _healthService = healthService;
        _parameterEditor = new ParameterEditorViewModel(simulator);

        Loaded += OnLoaded;
        Closed += OnClosed;
        _alarmService.Changed += OnAlarmChanged;
        ParameterEditorControl.CommitRequested += OnParameterCommitRequested;
        CommandPanelControl.CommandRequested += OnCommandRequested;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        _shell.Refresh();
        DeviceListControl.Devices = _shell.Devices;

        CommandPanelControl.Commands = await _simulator.GetCommandsAsync();
        await _parameterEditor.LoadAsync();
        ParameterEditorControl.Parameters = _parameterEditor.Parameters;

        AlarmPanelControl.Alarms = _shell.Alarms;
        RuntimeSummaryText.Text = $"Devices: {_shell.DeviceCount} · Alarms: {_shell.AlarmCount}";
        StatusText.Text = "Workbench runtime ready.";
    }

    private async void OnCommandRequested(DeviceCommandDescriptor command)
    {
        try
        {
            switch (command.Name)
            {
                case "connect":
                    await _simulator.ConnectAsync();
                    break;
                case "disconnect":
                    await _simulator.DisconnectAsync();
                    break;
                case "refresh":
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported Workbench simulator command '{command.Name}'.");
            }

            await RefreshAsync();
            StatusText.Text = $"{command.DisplayName}: completed.";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private async void OnParameterCommitRequested(ParameterEntry entry)
    {
        try
        {
            await _parameterEditor.CommitAsync(entry);
            await _parameterEditor.LoadAsync();
            StatusText.Text = $"{entry.DisplayName}: applied.";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private async void RefreshDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var reports = await _healthService.CheckAllAsync();
        HealthText.Text = reports.Count == 0
            ? "No health probes are registered yet."
            : string.Join(Environment.NewLine, reports.Select(report =>
                $"{report.Name}: {report.Status} - {report.Description}"));
    }

    private void OnAlarmChanged(Alarm _)
    {
        Dispatcher.Invoke(() =>
        {
            _shell.Refresh();
            AlarmPanelControl.Alarms = _shell.Alarms;
            RuntimeSummaryText.Text = $"Devices: {_shell.DeviceCount} · Alarms: {_shell.AlarmCount}";
        });
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _alarmService.Changed -= OnAlarmChanged;
        ParameterEditorControl.CommitRequested -= OnParameterCommitRequested;
        CommandPanelControl.CommandRequested -= OnCommandRequested;
        _shell.Dispose();
    }
}
