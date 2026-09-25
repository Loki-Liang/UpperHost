using System.ComponentModel;
using System.Runtime.CompilerServices;
using UpperHost.Abstractions.Devices;
using UpperHost.Abstractions.Diagnostics;

namespace UpperHost.Workbench.Application;

public sealed record WorkbenchSection(string Key, string DisplayName, string Description);

public sealed class WorkbenchShellViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly IReadOnlyList<WorkbenchSection> DefaultSections =
    [
        new("project", "Project", "Project lifecycle and persistence."),
        new("devices", "Devices", "Registered devices and current state."),
        new("connections", "Connections", "Physical connection ownership and status."),
        new("control", "Control", "Commands, parameters and device actions."),
        new("data", "Data", "Streaming data and waveform monitoring."),
        new("alarms", "Alarms", "Active alarm lifecycle."),
        new("logs", "Logs", "Application and device logs."),
        new("diagnostics", "Diagnostics", "Health and runtime diagnostics.")
    ];

    private readonly IDeviceRegistry _deviceRegistry;
    private readonly IAlarmService _alarmService;
    private string _selectedSectionKey = "project";
    private bool _disposed;

    public WorkbenchShellViewModel(IDeviceRegistry deviceRegistry, IAlarmService alarmService)
    {
        _deviceRegistry = deviceRegistry ?? throw new ArgumentNullException(nameof(deviceRegistry));
        _alarmService = alarmService ?? throw new ArgumentNullException(nameof(alarmService));
        _alarmService.Changed += OnAlarmChanged;
    }

    public IReadOnlyList<WorkbenchSection> Sections => DefaultSections;

    public IReadOnlyCollection<IDevice> Devices => _deviceRegistry.Devices;

    public IReadOnlyCollection<Alarm> Alarms => _alarmService.Active;

    public int DeviceCount => Devices.Count;

    public int AlarmCount => Alarms.Count;

    public string SelectedSectionKey
    {
        get => _selectedSectionKey;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Section key is required.", nameof(value));
            if (!Sections.Any(section => string.Equals(section.Key, value, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown Workbench section.");
            if (string.Equals(_selectedSectionKey, value, StringComparison.OrdinalIgnoreCase))
                return;

            _selectedSectionKey = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh()
    {
        OnPropertyChanged(nameof(Devices));
        OnPropertyChanged(nameof(Alarms));
        OnPropertyChanged(nameof(DeviceCount));
        OnPropertyChanged(nameof(AlarmCount));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _alarmService.Changed -= OnAlarmChanged;
        _disposed = true;
    }

    private void OnAlarmChanged(Alarm _) => Refresh();

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
