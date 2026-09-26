using System.Diagnostics.Metrics;
using UpperHost.Abstractions.Devices;
using UpperHost.Abstractions.Observability;
using UpperHost.Control.Commands;
using UpperHost.Control.Scheduling;

namespace UpperHost.Tests;

public sealed class CommandDispatcherReliabilityTests
{
    [Fact]
    public async Task Provider_fault_isolated_to_command_and_dispatcher_continues()
    {
        var target = new FaultOnceTarget();
        var runtime = new CommandRuntime<TestCommand, string>(target);
        await using var dispatcher = new BoundedCommandDispatcher<TestCommand, string>(
            runtime,
            new BoundedCommandDispatcherOptions(Capacity: 4, PerPriorityCapacity: 4, MaxConcurrency: 1));

        await dispatcher.StartAsync();

        var failed = await dispatcher.EnqueueAsync(new TestCommand("fail"));
        var succeeded = await dispatcher.EnqueueAsync(new TestCommand("ok"));

        Assert.Equal(CommandExecutionStatus.Faulted, failed.Status);
        Assert.True(succeeded.IsSuccess);
        Assert.Equal(2, target.CallCount);
    }

    [Fact]
    public async Task Stop_waits_for_running_command_and_rejects_new_admission()
    {
        var target = new BlockingTarget();
        var runtime = new CommandRuntime<TestCommand, string>(target);
        await using var dispatcher = new BoundedCommandDispatcher<TestCommand, string>(runtime);

        await dispatcher.StartAsync();

        var running = dispatcher.EnqueueAsync(new TestCommand("running"));
        await target.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stop = dispatcher.StopAsync();
        await Task.Yield();

        var rejected = await dispatcher.EnqueueAsync(new TestCommand("late"));
        Assert.Equal(CommandExecutionStatus.Rejected, rejected.Status);
        Assert.Equal("dispatcher_not_running", rejected.Code);
        Assert.False(stop.IsCompleted);

        target.Release.TrySetResult();

        Assert.True((await running).IsSuccess);
        await stop.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(dispatcher.IsRunning);
    }

    [Fact]
    public async Task Cancelled_resource_waiter_does_not_leak_or_block_following_command()
    {
        var target = new FirstCommandGateTarget();
        var runtime = new CommandRuntime<TestCommand, string>(target);
        await using var dispatcher = new BoundedCommandDispatcher<TestCommand, string>(
            runtime,
            new BoundedCommandDispatcherOptions(Capacity: 8, PerPriorityCapacity: 8, MaxConcurrency: 3));

        await dispatcher.StartAsync();
        var resource = new CommandResourceKey("device", "shared");

        var first = dispatcher.EnqueueAsync(
            new TestCommand("first"),
            new CommandDispatchOptions(Resources: [resource]));
        await target.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        using var cancelledCts = new CancellationTokenSource();
        var cancelled = dispatcher.EnqueueAsync(
            new TestCommand("cancelled"),
            new CommandDispatchOptions(Resources: [resource]),
            cancelledCts.Token);
        cancelledCts.Cancel();

        var third = dispatcher.EnqueueAsync(
            new TestCommand("third"),
            new CommandDispatchOptions(Resources: [resource]));

        target.ReleaseFirst.TrySetResult();

        Assert.True((await first).IsSuccess);
        Assert.Equal(CommandExecutionStatus.Cancelled, (await cancelled).Status);
        Assert.True((await third).IsSuccess);
    }

    [Fact]
    public async Task Dispatcher_emits_low_cardinality_queue_and_rejection_metrics()
    {
        var pendingIncrementSeen = 0;
        var pendingDecrementSeen = 0;
        long rejections = 0;
        var queueWaitMeasurements = 0;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == UpperHostTelemetry.InstrumentationName &&
                instrument.Name.StartsWith("upperhost.command.dispatcher.", StringComparison.Ordinal))
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == "upperhost.command.dispatcher.pending")
            {
                if (measurement > 0)
                    Interlocked.Exchange(ref pendingIncrementSeen, 1);
                if (measurement < 0)
                    Interlocked.Exchange(ref pendingDecrementSeen, 1);
            }
            if (instrument.Name == "upperhost.command.dispatcher.admission_rejections")
                Interlocked.Add(ref rejections, measurement);

            Assert.DoesNotContain(tags.ToArray(), tag =>
                tag.Key.Contains("device", StringComparison.OrdinalIgnoreCase) ||
                tag.Key.Contains("resource", StringComparison.OrdinalIgnoreCase) ||
                tag.Key.Contains("execution", StringComparison.OrdinalIgnoreCase));
        });
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == "upperhost.command.dispatcher.queue_wait")
                Interlocked.Increment(ref queueWaitMeasurements);
        });
        listener.Start();

        var target = new BlockingTarget();
        var runtime = new CommandRuntime<TestCommand, string>(target);
        await using var dispatcher = new BoundedCommandDispatcher<TestCommand, string>(
            runtime,
            new BoundedCommandDispatcherOptions(Capacity: 1, PerPriorityCapacity: 2, MaxConcurrency: 1));

        await dispatcher.StartAsync();

        var first = dispatcher.EnqueueAsync(new TestCommand("first"));
        await target.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var queued = dispatcher.EnqueueAsync(new TestCommand("queued"));
        await WaitUntilAsync(() => dispatcher.PendingCount == 1);

        var rejected = await dispatcher.EnqueueAsync(
            new TestCommand("rejected"),
            new CommandDispatchOptions(AdmissionMode: CommandAdmissionMode.Reject));
        Assert.Equal(CommandExecutionStatus.Rejected, rejected.Status);

        target.Release.TrySetResult();
        await Task.WhenAll(first, queued);

        Assert.Equal(1, Volatile.Read(ref pendingIncrementSeen));
        Assert.Equal(1, Volatile.Read(ref pendingDecrementSeen));
        Assert.True(Volatile.Read(ref rejections) >= 1);
        Assert.True(Volatile.Read(ref queueWaitMeasurements) >= 1);
        Assert.True(dispatcher.PendingHighWater >= 1);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
            await Task.Delay(1, timeout.Token);
    }

    private sealed record TestCommand(string Name);

    private sealed class FaultOnceTarget : ICommandable<TestCommand, string>
    {
        private int _calls;
        public int CallCount => Volatile.Read(ref _calls);

        public Task<string> ExecuteAsync(TestCommand command, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
                throw new IOException("Injected provider fault.");

            return Task.FromResult(command.Name);
        }
    }

    private sealed class BlockingTarget : ICommandable<TestCommand, string>
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> ExecuteAsync(TestCommand command, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return command.Name;
        }
    }

    private sealed class FirstCommandGateTarget : ICommandable<TestCommand, string>
    {
        private int _calls;
        public TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> ExecuteAsync(TestCommand command, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                FirstStarted.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(cancellationToken);
            }

            return command.Name;
        }
    }
}
