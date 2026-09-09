using UpperHost.Abstractions.Devices;
using UpperHost.Control.Commands;
using UpperHost.Control.Recipes;
using UpperHost.Control.Scheduling;

namespace UpperHost.Tests;

public sealed class SchedulingAndRecipeTests
{
    [Fact]
    public async Task Scheduler_prioritizes_pending_commands_without_preempting_running_command()
    {
        var target = new OrderingTarget();
        var runtime = new CommandRuntime<ScheduledTestCommand, string>(target);
        await using var scheduler = new CommandScheduler<ScheduledTestCommand, string>(runtime);

        var first = scheduler.EnqueueAsync(new ScheduledTestCommand("first", Block: true));
        await target.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var low = scheduler.EnqueueAsync(new ScheduledTestCommand("low"), CommandPriority.Low);
        var critical = scheduler.EnqueueAsync(new ScheduledTestCommand("critical"), CommandPriority.Critical);

        target.ReleaseFirst.TrySetResult();
        await Task.WhenAll(first, low, critical);

        Assert.Equal(["first", "critical", "low"], target.ExecutionOrder);
    }

    [Fact]
    public async Task Scheduler_executes_only_one_command_at_a_time()
    {
        var target = new ConcurrencyTarget();
        var runtime = new CommandRuntime<ScheduledTestCommand, string>(target);
        await using var scheduler = new CommandScheduler<ScheduledTestCommand, string>(runtime);

        var tasks = Enumerable.Range(0, 5)
            .Select(i => scheduler.EnqueueAsync(new ScheduledTestCommand(i.ToString())))
            .ToArray();

        await Task.WhenAll(tasks);
        Assert.Equal(1, target.MaximumConcurrency);
    }

    [Fact]
    public async Task Recipe_applies_parameters_across_registered_devices_with_readback()
    {
        var first = new ParameterDevice("device-1", 10);
        var second = new ParameterDevice("device-2", 20);
        var registry = new TestRegistry(first, second);
        var applier = new RecipeApplier(registry);
        var recipe = RecipeDefinition.Create(
            "product-a",
            new RecipeParameterValue("device-1", "setpoint", 31d),
            new RecipeParameterValue("device-2", "setpoint", 42d));

        var result = await applier.ApplyAsync(recipe);

        Assert.True(result.IsSuccess);
        Assert.Equal(31d, first.Value);
        Assert.Equal(42d, second.Value);
    }

    [Fact]
    public async Task Recipe_stops_after_first_failure_by_default()
    {
        var first = new ParameterDevice("device-1", 10) { RejectWrites = true };
        var second = new ParameterDevice("device-2", 20);
        var registry = new TestRegistry(first, second);
        var applier = new RecipeApplier(registry);
        var recipe = RecipeDefinition.Create(
            "bad-recipe",
            new RecipeParameterValue("device-1", "setpoint", 31d),
            new RecipeParameterValue("device-2", "setpoint", 42d));

        var result = await applier.ApplyAsync(recipe);

        Assert.False(result.IsSuccess);
        Assert.Single(result.Items);
        Assert.Equal(20d, second.Value);
    }

    private sealed record ScheduledTestCommand(string Name, bool Block = false);

    private sealed class OrderingTarget : ICommandable<ScheduledTestCommand, string>
    {
        private readonly List<string> _executionOrder = [];
        private readonly object _gate = new();
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> ExecutionOrder
        {
            get
            {
                lock (_gate)
                    return _executionOrder.ToArray();
            }
        }

        public async Task<string> ExecuteAsync(ScheduledTestCommand command, CancellationToken cancellationToken = default)
        {
            lock (_gate)
                _executionOrder.Add(command.Name);

            if (command.Block)
            {
                FirstStarted.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(cancellationToken);
            }

            return command.Name;
        }
    }

    private sealed class ConcurrencyTarget : ICommandable<ScheduledTestCommand, string>
    {
        private int _active;
        private int _maximum;
        public int MaximumConcurrency => Volatile.Read(ref _maximum);

        public async Task<string> ExecuteAsync(ScheduledTestCommand command, CancellationToken cancellationToken = default)
        {
            var current = Interlocked.Increment(ref _active);
            UpdateMaximum(current);
            try
            {
                await Task.Delay(10, cancellationToken);
                return command.Name;
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private void UpdateMaximum(int current)
        {
            while (true)
            {
                var observed = Volatile.Read(ref _maximum);
                if (current <= observed || Interlocked.CompareExchange(ref _maximum, current, observed) == observed)
                    return;
            }
        }
    }

    private sealed class ParameterDevice : IDevice, IParameterProvider
    {
        public ParameterDevice(string id, double value)
        {
            Descriptor = new DeviceDescriptor(id, id);
            Value = value;
        }

        public DeviceDescriptor Descriptor { get; }
        public DeviceState State => DeviceState.Online;
        public IReadOnlyCollection<string> Capabilities { get; } = ["parameters"];
        public double Value { get; private set; }
        public bool RejectWrites { get; init; }

        public Task<IReadOnlyList<DeviceParameterDescriptor>> GetParametersAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<DeviceParameterDescriptor> values =
                [new("setpoint", "Setpoint", DeviceParameterKind.Decimal, Value)];
            return Task.FromResult(values);
        }

        public Task SetParameterAsync(string key, object? value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RejectWrites)
                throw new InvalidOperationException("Device rejected recipe value.");
            Value = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
            return Task.CompletedTask;
        }
    }

    private sealed class TestRegistry(params IDevice[] devices) : IDeviceRegistry
    {
        private readonly Dictionary<string, IDevice> _devices = devices.ToDictionary(device => device.Descriptor.Id);
        public IReadOnlyCollection<IDevice> Devices => _devices.Values;
        public bool Register(IDevice device) => _devices.TryAdd(device.Descriptor.Id, device);
        public bool Unregister(string deviceId) => _devices.Remove(deviceId);
        public bool TryGet(string deviceId, out IDevice? device) => _devices.TryGetValue(deviceId, out device);
    }
}
