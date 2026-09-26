using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using UpperHost.Abstractions.Devices;
using UpperHost.Control.Parameters;
using UpperHost.Control.Scheduling;
using UpperHost.Control.State;

namespace UpperHost.Tests;

public sealed class TypedParameterRuntimeTests
{
    [Fact]
    public async Task Direct_read_uses_direct_provider_and_updates_typed_snapshot()
    {
        var provider = new DirectProvider("speed", 1200);
        var services = CreateArbiterServices();
        await using var serviceProvider = services.BuildServiceProvider();
        var arbiter = serviceProvider.GetRequiredService<ICommandResourceArbiter>();
        await using var snapshots = new DeviceSnapshotStore<int>();
        var epoch = new FixedEpochSource(7);
        var runtime = new TypedParameterRuntime<int>(
            "device-1", provider, arbiter, snapshots, epoch);

        var result = await runtime.ReadAsync(
            new ParameterContract<int>(
                "speed",
                Range: new ParameterRange<int>(0, 3000)));

        Assert.Equal(1200, result.Value);
        Assert.Equal(1, provider.DirectReadCount);
        Assert.Equal(0, provider.EnumerationReadCount);

        var snapshot = snapshots.Get(
            "device-1",
            new DeviceStatePartitionKey("parameter:speed"));
        Assert.NotNull(snapshot);
        Assert.Equal(1200, snapshot.Value);
        Assert.Equal(7, snapshot.ConnectionEpoch);
    }

    [Fact]
    public async Task Invalid_value_is_rejected_before_provider_write()
    {
        var provider = new DirectProvider("speed", 1000);
        var services = CreateArbiterServices();
        await using var serviceProvider = services.BuildServiceProvider();
        var arbiter = serviceProvider.GetRequiredService<ICommandResourceArbiter>();
        await using var snapshots = new DeviceSnapshotStore<int>();
        var runtime = new TypedParameterRuntime<int>(
            "device-1", provider, arbiter, snapshots, new FixedEpochSource(1));

        var result = await runtime.WriteAsync(
            new ParameterContract<int>(
                "speed",
                Range: new ParameterRange<int>(0, 2000)),
            5000);

        Assert.Equal(TypedParameterWriteStatus.Rejected, result.Status);
        Assert.Equal("parameter_validation_failed", result.Code);
        Assert.Equal(0, provider.WriteCount);
    }

    [Fact]
    public async Task Parameter_specific_tolerance_controls_readback_verification()
    {
        var provider = new DirectProvider("gain", 1.0)
        {
            TransformWrite = value => (double)value! + 0.0005
        };
        var services = CreateArbiterServices();
        await using var serviceProvider = services.BuildServiceProvider();
        var arbiter = serviceProvider.GetRequiredService<ICommandResourceArbiter>();
        await using var snapshots = new DeviceSnapshotStore<double>();
        var runtime = new TypedParameterRuntime<double>(
            "device-1", provider, arbiter, snapshots, new FixedEpochSource(1));

        var contract = new ParameterContract<double>(
            "gain",
            ReadbackComparer: ParameterComparers.Absolute(0.001));

        var result = await runtime.WriteAsync(contract, 2.0);

        Assert.Equal(TypedParameterWriteStatus.Verified, result.Status);
        Assert.Equal(2.0005, result.ObservedValue);
        Assert.Equal(1, provider.WriteCount);
        Assert.NotNull(result.Snapshot);
    }

    [Fact]
    public async Task Readback_timeout_after_write_returns_unknown_without_faking_snapshot()
    {
        var time = new FakeTimeProvider();
        var provider = new DirectProvider("target", 10)
        {
            BlockReadbackAfterWrite = true
        };
        var services = CreateArbiterServices();
        await using var serviceProvider = services.BuildServiceProvider();
        var arbiter = serviceProvider.GetRequiredService<ICommandResourceArbiter>();
        await using var snapshots = new DeviceSnapshotStore<int>(time);
        var runtime = new TypedParameterRuntime<int>(
            "device-1", provider, arbiter, snapshots, new FixedEpochSource(1), time);

        var write = runtime.WriteAsync(
            new ParameterContract<int>("target"),
            20,
            new TypedParameterWriteOptions(
                ReadbackTimeout: TimeSpan.FromSeconds(5)));

        await provider.ReadbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        time.Advance(TimeSpan.FromSeconds(6));

        var result = await write.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(TypedParameterWriteStatus.UnknownOutcome, result.Status);
        Assert.Equal("parameter_readback_timeout", result.Code);
        Assert.Null(snapshots.Get(
            "device-1",
            new DeviceStatePartitionKey("parameter:target")));
    }

    [Fact]
    public async Task Write_and_readback_hold_shared_control_resource_against_commands()
    {
        var services = new ServiceCollection();
        var commandTarget = new BlockingCommandTarget();
        services.AddSingleton<ICommandable<TestCommand, string>>(commandTarget);
        services.AddUpperHostCommandDispatcher<TestCommand, string>(
            new BoundedCommandDispatcherOptions(MaxConcurrency: 2));

        await using var serviceProvider = services.BuildServiceProvider();
        var dispatcher = serviceProvider.GetRequiredService<BoundedCommandDispatcher<TestCommand, string>>();
        var arbiter = serviceProvider.GetRequiredService<ICommandResourceArbiter>();
        await dispatcher.StartAsync();

        var resource = new CommandResourceKey("device", "device-1");
        var blocking = dispatcher.EnqueueAsync(
            new TestCommand("move"),
            new CommandDispatchOptions(Resources: [resource]));
        await commandTarget.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var provider = new DirectProvider("speed", 1000);
        await using var snapshots = new DeviceSnapshotStore<int>();
        var parameters = new TypedParameterRuntime<int>(
            "device-1", provider, arbiter, snapshots, new FixedEpochSource(1));

        var write = parameters.WriteAsync(
            new ParameterContract<int>("speed"),
            1500,
            new TypedParameterWriteOptions(
                ResourceClaims:
                [
                    new CommandResourceClaim(resource, CommandResourceAccess.Exclusive)
                ]));

        await Task.Yield();
        Assert.Equal(0, provider.WriteCount);

        commandTarget.Release.TrySetResult();
        Assert.True((await blocking).IsSuccess);

        var result = await write.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.Verified);
        Assert.Equal(1, provider.WriteCount);
    }

    [Fact]
    public async Task Direct_read_rejects_result_if_epoch_changes_before_commit()
    {
        var provider = new EpochRaceProvider("speed", 1200);
        var services = CreateArbiterServices();
        await using var serviceProvider = services.BuildServiceProvider();
        var arbiter = serviceProvider.GetRequiredService<ICommandResourceArbiter>();
        await using var snapshots = new DeviceSnapshotStore<int>();
        var epoch = new MutableEpochSource { CurrentEpoch = 1 };
        var runtime = new TypedParameterRuntime<int>(
            "device-1", provider, arbiter, snapshots, epoch);

        var read = runtime.ReadAsync(new ParameterContract<int>("speed"));
        await provider.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        epoch.CurrentEpoch = 2;
        provider.ReleaseRead.TrySetResult();

        var exception = await Assert.ThrowsAsync<DeviceEpochChangedException>(() => read);
        Assert.Equal(1, exception.StartedEpoch);
        Assert.Equal(2, exception.CurrentEpoch);
        Assert.Null(snapshots.Get(
            "device-1",
            new DeviceStatePartitionKey("parameter:speed")));
    }

    [Fact]
    public void Double_comparers_handle_nan_infinity_and_relative_values()
    {
        Assert.True(ParameterComparers.Absolute(0.01).AreEquivalent(double.NaN, double.NaN));
        Assert.True(ParameterComparers.Exact<double>().AreEquivalent(
            double.PositiveInfinity,
            double.PositiveInfinity));
        Assert.True(ParameterComparers.Relative(0.01).AreEquivalent(100.0, 100.5));
        Assert.False(ParameterComparers.Relative(0.001).AreEquivalent(100.0, 100.5));
    }

    private static ServiceCollection CreateArbiterServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICommandable<NoOpCommand, string>, NoOpTarget>();
        services.AddUpperHostCommandDispatcher<NoOpCommand, string>();
        return services;
    }

    private sealed record NoOpCommand;
    private sealed class NoOpTarget : ICommandable<NoOpCommand, string>
    {
        public Task<string> ExecuteAsync(
            NoOpCommand command,
            CancellationToken cancellationToken = default) =>
            Task.FromResult("ok");
    }

    private sealed class FixedEpochSource(long epoch) : IDeviceConnectionEpochSource
    {
        public long GetCurrentEpoch(string deviceId) => epoch;
    }

    private sealed class MutableEpochSource : IDeviceConnectionEpochSource
    {
        public long CurrentEpoch { get; set; }
        public long GetCurrentEpoch(string deviceId) => CurrentEpoch;
    }

    private sealed class EpochRaceProvider(string key, object? value) : IDirectParameterProvider
    {
        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<DeviceParameterDescriptor>> GetParametersAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DeviceParameterDescriptor>>(
            [
                new DeviceParameterDescriptor(
                    key, key, DeviceParameterKind.Integer, value)
            ]);

        public async Task<DeviceParameterDescriptor> GetParameterAsync(
            string requestedKey,
            CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await ReleaseRead.Task.WaitAsync(cancellationToken);
            return new DeviceParameterDescriptor(
                key, key, DeviceParameterKind.Integer, value);
        }

        public Task SetParameterAsync(
            string requestedKey,
            object? newValue,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class DirectProvider(string key, object? value) : IDirectParameterProvider
    {
        private object? _value = value;
        private int _writes;
        private int _directReads;
        private int _enumerationReads;

        public Func<object?, object?>? TransformWrite { get; init; }
        public bool BlockReadbackAfterWrite { get; init; }
        public TaskCompletionSource ReadbackStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int WriteCount => Volatile.Read(ref _writes);
        public int DirectReadCount => Volatile.Read(ref _directReads);
        public int EnumerationReadCount => Volatile.Read(ref _enumerationReads);

        public Task<IReadOnlyList<DeviceParameterDescriptor>> GetParametersAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _enumerationReads);
            IReadOnlyList<DeviceParameterDescriptor> result =
            [
                Descriptor()
            ];
            return Task.FromResult(result);
        }

        public async Task<DeviceParameterDescriptor> GetParameterAsync(
            string requestedKey,
            CancellationToken cancellationToken = default)
        {
            if (!requestedKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                throw new KeyNotFoundException(requestedKey);

            Interlocked.Increment(ref _directReads);

            if (BlockReadbackAfterWrite && Volatile.Read(ref _writes) > 0)
            {
                ReadbackStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return Descriptor();
        }

        public Task SetParameterAsync(
            string requestedKey,
            object? newValue,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!requestedKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                throw new KeyNotFoundException(requestedKey);

            Interlocked.Increment(ref _writes);
            _value = TransformWrite?.Invoke(newValue) ?? newValue;
            return Task.CompletedTask;
        }

        private DeviceParameterDescriptor Descriptor() =>
            new(
                key,
                key,
                _value switch
                {
                    int => DeviceParameterKind.Integer,
                    double => DeviceParameterKind.Decimal,
                    _ => DeviceParameterKind.Text
                },
                _value);
    }

    private sealed record TestCommand(string Name);

    private sealed class BlockingCommandTarget : ICommandable<TestCommand, string>
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> ExecuteAsync(
            TestCommand command,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return command.Name;
        }
    }
}
