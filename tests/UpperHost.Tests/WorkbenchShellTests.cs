using UpperHost.Abstractions.Devices;
using UpperHost.Abstractions.Diagnostics;
using UpperHost.Workbench.Application;

namespace UpperHost.Tests;

public sealed class WorkbenchShellTests
{
    [Fact]
    public void Shell_exposes_stable_sections_and_runtime_snapshot()
    {
        var device = new FakeDevice();
        var registry = new FakeRegistry(device);
        var alarms = new FakeAlarmService();
        using var shell = new WorkbenchShellViewModel(registry, alarms);

        Assert.Equal(
            new[] { "project", "devices", "connections", "control", "data", "alarms", "logs", "diagnostics" },
            shell.Sections.Select(section => section.Key).ToArray());
        Assert.Equal("project", shell.SelectedSectionKey);
        Assert.Equal(1, shell.DeviceCount);
        Assert.Empty(shell.Alarms);

        shell.SelectedSectionKey = "devices";
        Assert.Equal("devices", shell.SelectedSectionKey);
        Assert.Throws<ArgumentOutOfRangeException>(() => shell.SelectedSectionKey = "workflow");
    }

    [Fact]
    public async Task Alarm_change_refreshes_shell_alarm_count()
    {
        var registry = new FakeRegistry(new FakeDevice());
        var alarms = new FakeAlarmService();
        using var shell = new WorkbenchShellViewModel(registry, alarms);
        var changed = new List<string>();
        shell.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not null)
                changed.Add(args.PropertyName);
        };

        await alarms.RaiseAsync("workbench", "SIM", "simulated alarm", AlarmSeverity.Warning);

        Assert.Equal(1, shell.AlarmCount);
        Assert.Contains(nameof(WorkbenchShellViewModel.AlarmCount), changed);
        Assert.Contains(nameof(WorkbenchShellViewModel.Alarms), changed);
    }

    private sealed class FakeDevice : IDevice
    {
        public DeviceDescriptor Descriptor { get; } = new("test-device", "Test Device");
        public DeviceState State => DeviceState.Online;
        public IReadOnlyCollection<string> Capabilities { get; } = ["test"];
    }

    private sealed class FakeRegistry(params IDevice[] devices) : IDeviceRegistry
    {
        private readonly Dictionary<string, IDevice> _devices =
            devices.ToDictionary(device => device.Descriptor.Id, StringComparer.OrdinalIgnoreCase);

        public IReadOnlyCollection<IDevice> Devices => _devices.Values;

        public bool Register(IDevice device) => _devices.TryAdd(device.Descriptor.Id, device);

        public bool Unregister(string deviceId) => _devices.Remove(deviceId);

        public bool TryGet(string deviceId, out IDevice? device) => _devices.TryGetValue(deviceId, out device);
    }

    private sealed class FakeAlarmService : IAlarmService
    {
        private readonly List<Alarm> _active = [];

        public IReadOnlyCollection<Alarm> Active => _active.ToArray();

        public event Action<Alarm>? Changed;

        public ValueTask<Alarm> RaiseAsync(
            string source,
            string code,
            string message,
            AlarmSeverity severity,
            IReadOnlyDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var alarm = new Alarm(
                Guid.NewGuid(),
                source,
                code,
                message,
                severity,
                AlarmStatus.Raised,
                DateTimeOffset.UtcNow,
                Metadata: metadata);
            _active.Add(alarm);
            Changed?.Invoke(alarm);
            return ValueTask.FromResult(alarm);
        }

        public ValueTask<bool> AcknowledgeAsync(Guid id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(false);
        }

        public ValueTask<bool> ClearAsync(Guid id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var alarm = _active.FirstOrDefault(candidate => candidate.Id == id);
            if (alarm is null)
                return ValueTask.FromResult(false);

            _active.Remove(alarm);
            Changed?.Invoke(alarm);
            return ValueTask.FromResult(true);
        }
    }
}
