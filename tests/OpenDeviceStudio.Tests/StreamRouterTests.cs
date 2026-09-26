using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using OpenDeviceStudio.Abstractions.Observability;
using OpenDeviceStudio.Dataflow;

namespace OpenDeviceStudio.Tests;

public sealed class StreamRouterTests
{
    [Fact]
    public async Task InvalidRequiredLossyAndOptionalWaitConfigurationsFailFast()
    {
        await using var router = new StreamRouter<int>();

        Assert.Throws<ArgumentException>(() => router.RegisterBranch(
            new StreamBranchOptions(
                "raw",
                "Raw",
                Capacity: 8,
                Delivery: StreamBranchDelivery.Required,
                Overflow: StreamOverflowPolicy.DropOldest,
                FailurePolicy: StreamBranchFailurePolicy.Propagate),
            static (_, _) => ValueTask.CompletedTask));

        Assert.Throws<ArgumentException>(() => router.RegisterBranch(
            new StreamBranchOptions(
                "ui",
                "UI",
                Capacity: 8,
                Delivery: StreamBranchDelivery.Optional,
                Overflow: StreamOverflowPolicy.Wait,
                FailurePolicy: StreamBranchFailurePolicy.Isolate),
            static (_, _) => ValueTask.CompletedTask));
    }

    [Fact]
    public async Task StartSealsRequiredTopologyButAllowsOptionalAttachDetach()
    {
        var requiredSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var optionalSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var router = new StreamRouter<int>();
        router.RegisterBranch(
            RequiredOptions("processing", capacity: 4),
            (item, _) =>
            {
                if (item.Value == 7)
                    requiredSeen.TrySetResult();
                return ValueTask.CompletedTask;
            });

        router.Start();

        Assert.Throws<InvalidOperationException>(() => router.RegisterBranch(
            RequiredOptions("late-required", capacity: 4),
            static (_, _) => ValueTask.CompletedTask));

        var optional = router.RegisterBranch(
            OptionalOptions("presentation", capacity: 1, overflow: StreamOverflowPolicy.Latest),
            (item, _) =>
            {
                if (item.Value == 7)
                    optionalSeen.TrySetResult();
                return ValueTask.CompletedTask;
            });

        var result = await router.PublishAsync(7);

        await requiredSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await optionalSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(StreamRouterState.Running, result.RouterState);
        Assert.All(result.Branches, static branch => Assert.Equal(StreamBranchPublishStatus.Accepted, branch.Status));

        await optional.DetachAsync(StreamCompletionMode.Drain);

        var snapshot = router.GetSnapshot();
        Assert.Equal(1, snapshot.BranchCount);
        Assert.Equal(1, snapshot.RequiredBranchCount);
        Assert.Equal(0, snapshot.OptionalBranchCount);

        var completed = await router.CompleteAsync();
        Assert.Equal(StreamRouterState.Completed, completed.State);
    }

    [Fact]
    public async Task SlowOptionalBranchDropsOldestWithoutBlockingRequiredBranch()
    {
        var requiredValues = new ConcurrentQueue<int>();
        var optionalValues = new ConcurrentQueue<int>();
        var requiredDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var optionalEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var optionalRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var optionalDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var router = new StreamRouter<int>();
        router.RegisterBranch(
            RequiredOptions("required", capacity: 4),
            (item, _) =>
            {
                requiredValues.Enqueue(item.Value);
                if (requiredValues.Count == 3)
                    requiredDone.TrySetResult();
                return ValueTask.CompletedTask;
            });

        var optional = router.RegisterBranch(
            OptionalOptions("ui", capacity: 1, overflow: StreamOverflowPolicy.DropOldest),
            async (item, cancellationToken) =>
            {
                optionalValues.Enqueue(item.Value);
                if (item.Value == 1)
                {
                    optionalEntered.TrySetResult();
                    await optionalRelease.Task.WaitAsync(cancellationToken);
                }

                if (item.Value == 3)
                    optionalDone.TrySetResult();
            });

        router.Start();

        await router.PublishAsync(1);
        await optionalEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await router.PublishAsync(2);
        var third = await router.PublishAsync(3);

        var uiResult = Assert.Single(third.Branches, static branch => branch.BranchId == "ui");
        Assert.Equal(StreamBranchPublishStatus.Accepted, uiResult.Status);
        Assert.Equal(1, uiResult.DroppedCount);

        optionalRelease.TrySetResult();

        await requiredDone.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await optionalDone.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new[] { 1, 2, 3 }, requiredValues.ToArray());
        Assert.Equal(new[] { 1, 3 }, optionalValues.ToArray());
        Assert.Equal(1, optional.GetSnapshot().Dropped);
        Assert.InRange(optional.GetSnapshot().HighWatermark, 0, 1);

        await router.CompleteAsync();
    }

    [Fact]
    public async Task RequiredRejectProducesExplicitPartialPublishAndFaultsRouter()
    {
        var aValues = new ConcurrentQueue<int>();
        var bEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var router = new StreamRouter<int>();
        router.RegisterBranch(
            RequiredOptions("a", capacity: 8),
            (item, _) =>
            {
                aValues.Enqueue(item.Value);
                return ValueTask.CompletedTask;
            });

        var b = router.RegisterBranch(
            RequiredOptions("b", capacity: 1, overflow: StreamOverflowPolicy.Reject),
            async (item, cancellationToken) =>
            {
                if (item.Value == 1)
                {
                    bEntered.TrySetResult();
                    await bRelease.Task.WaitAsync(cancellationToken);
                }
            });

        router.Start();

        await router.PublishAsync(1);
        await bEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await router.PublishAsync(2);
        var result = await router.PublishAsync(3);

        Assert.True(result.HasRequiredFailure);
        Assert.True(result.IsPartialRequiredDelivery);
        Assert.Equal(StreamRouterState.Faulted, result.RouterState);

        var aResult = Assert.Single(result.Branches, static branch => branch.BranchId == "a");
        var bResult = Assert.Single(result.Branches, static branch => branch.BranchId == "b");
        Assert.Equal(StreamBranchPublishStatus.Accepted, aResult.Status);
        Assert.Equal(StreamBranchPublishStatus.Rejected, bResult.Status);
        Assert.Equal(1, b.GetSnapshot().Rejected);

        bRelease.TrySetResult();
        var completed = await router.CompleteAsync(StreamCompletionMode.Drain);
        Assert.Equal(StreamRouterState.Faulted, completed.State);
        Assert.Contains(3, aValues);
    }

    [Fact]
    public async Task RequiredConsumerFaultIsObservedAndFaultsRouter()
    {
        await using var router = new StreamRouter<int>();
        var required = router.RegisterBranch(
            RequiredOptions("algorithm", capacity: 4),
            static (_, _) => throw new InvalidOperationException("algorithm failed"));

        router.Start();

        var publish = await router.PublishAsync(1);
        Assert.Equal(StreamBranchPublishStatus.Accepted, Assert.Single(publish.Branches).Status);

        await required.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(StreamRouterState.Faulted, router.State);
        Assert.True(required.GetSnapshot().IsFaulted);
        var faultMessage = required.GetSnapshot().FaultMessage;
        Assert.NotNull(faultMessage);
        Assert.Contains("algorithm failed", faultMessage);

        var terminal = await router.CompleteAsync(StreamCompletionMode.Drain);
        Assert.Equal(StreamRouterState.Faulted, terminal.State);
    }

    [Fact]
    public async Task OptionalConsumerFaultIsIsolatedAndRequiredBranchContinues()
    {
        var requiredValues = new ConcurrentQueue<int>();
        var requiredDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var router = new StreamRouter<int>();
        router.RegisterBranch(
            RequiredOptions("required", capacity: 4),
            (item, _) =>
            {
                requiredValues.Enqueue(item.Value);
                if (requiredValues.Count == 2)
                    requiredDone.TrySetResult();
                return ValueTask.CompletedTask;
            });

        var optional = router.RegisterBranch(
            OptionalOptions("ui", capacity: 1, overflow: StreamOverflowPolicy.DropNewest),
            static (_, _) => throw new InvalidOperationException("render failed"));

        router.Start();

        await router.PublishAsync(1);
        await optional.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(StreamRouterState.Running, router.State);
        Assert.Equal(1, router.GetSnapshot().BranchCount);
        Assert.Equal(0, router.GetSnapshot().OptionalBranchCount);

        var second = await router.PublishAsync(2);
        Assert.Single(second.Branches);
        Assert.Equal("required", second.Branches[0].BranchId);

        await requiredDone.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { 1, 2 }, requiredValues.ToArray());

        var terminal = await router.CompleteAsync();
        Assert.Equal(StreamRouterState.Completed, terminal.State);
    }

    [Fact]
    public async Task WaitCancellationDoesNotWriteItemLater()
    {
        var values = new ConcurrentQueue<int>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var router = new StreamRouter<int>();
        router.RegisterBranch(
            RequiredOptions("required", capacity: 1, overflow: StreamOverflowPolicy.Wait),
            async (item, cancellationToken) =>
            {
                values.Enqueue(item.Value);
                if (item.Value == 1)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken);
                }
            });

        router.Start();

        await router.PublishAsync(1);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await router.PublishAsync(2);

        using var cancellation = new CancellationTokenSource();
        var pending = router.PublishAsync(3, cancellation.Token).AsTask();
        cancellation.Cancel();

        var cancelled = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        var branch = Assert.Single(cancelled.Branches);
        Assert.Equal(StreamBranchPublishStatus.Cancelled, branch.Status);
        Assert.True(cancelled.HasRequiredFailure);

        release.TrySetResult();
        await router.CompleteAsync(StreamCompletionMode.Drain);

        Assert.Equal(new[] { 1, 2 }, values.ToArray());
    }

    [Fact]
    public async Task OwnershipIsReleasedForDeliveredDroppedAndCancelledItems()
    {
        var retained = 0;
        var released = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var ownership = new RetainReleaseStreamOwnershipAdapter<TrackedItem>(
            item =>
            {
                Interlocked.Increment(ref retained);
                return item;
            },
            _ => Interlocked.Increment(ref released));

        await using var router = new StreamRouter<TrackedItem>();
        var optional = router.RegisterBranch(
            OptionalOptions("ui", capacity: 1, overflow: StreamOverflowPolicy.DropOldest),
            async (item, cancellationToken) =>
            {
                if (item.Value.Id == 1)
                {
                    entered.TrySetResult();
                    await neverRelease.Task.WaitAsync(cancellationToken);
                }
            },
            ownership);

        router.Start();

        await router.PublishAsync(new TrackedItem(1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await router.PublishAsync(new TrackedItem(2));
        var third = await router.PublishAsync(new TrackedItem(3));

        Assert.Equal(1, Assert.Single(third.Branches).DroppedCount);

        await optional.DetachAsync(StreamCompletionMode.Cancel);

        Assert.Equal(3, Volatile.Read(ref retained));
        Assert.Equal(3, Volatile.Read(ref released));

        await router.CompleteAsync();
    }

    [Fact]
    public async Task SerializedMultiPublisherPreservesSameSequenceOnRequiredBranches()
    {
        var first = new ConcurrentQueue<long>();
        var second = new ConcurrentQueue<long>();

        await using var router = new StreamRouter<int>();
        router.RegisterBranch(
            RequiredOptions("a", capacity: 64),
            (item, _) =>
            {
                first.Enqueue(item.PublishSequence);
                return ValueTask.CompletedTask;
            });
        router.RegisterBranch(
            RequiredOptions("b", capacity: 64),
            (item, _) =>
            {
                second.Enqueue(item.PublishSequence);
                return ValueTask.CompletedTask;
            });

        router.Start();

        var publishes = Enumerable.Range(0, 32)
            .Select(value => router.PublishAsync(value).AsTask())
            .ToArray();

        await Task.WhenAll(publishes);
        await router.CompleteAsync(StreamCompletionMode.Drain);

        var firstSequence = first.ToArray();
        var secondSequence = second.ToArray();

        Assert.Equal(32, firstSequence.Length);
        Assert.Equal(firstSequence, secondSequence);
        Assert.Equal(Enumerable.Range(1, 32).Select(static value => (long)value), firstSequence);
    }

    [Fact]
    public async Task DrainCompletesAllAcceptedItemsAndLeavesBoundedDiagnostics()
    {
        var delivered = 0;

        await using var router = new StreamRouter<int>();
        var branch = router.RegisterBranch(
            RequiredOptions("required", capacity: 8),
            (_, _) =>
            {
                Interlocked.Increment(ref delivered);
                return ValueTask.CompletedTask;
            });

        router.Start();

        for (var index = 0; index < 50; index++)
        {
            await router.PublishAsync(index);
            var live = branch.GetSnapshot();
            Assert.InRange(live.QueueDepth, 0, live.Capacity);
            Assert.InRange(live.HighWatermark, 0, live.Capacity);
        }

        var terminal = await router.CompleteAsync(StreamCompletionMode.Drain);
        var snapshot = branch.GetSnapshot();

        Assert.Equal(StreamRouterState.Completed, terminal.State);
        Assert.Equal(StreamBranchState.Completed, snapshot.State);
        Assert.Equal(50, Volatile.Read(ref delivered));
        Assert.Equal(50, snapshot.Accepted);
        Assert.Equal(50, snapshot.Delivered);
        Assert.Equal(0, snapshot.Dropped);
        Assert.Equal(0, snapshot.Rejected);
        Assert.Equal(0, snapshot.QueueDepth);
        Assert.InRange(snapshot.HighWatermark, 0, snapshot.Capacity);
        Assert.True(snapshot.LastQueueLatency >= TimeSpan.Zero);
        Assert.True(snapshot.MaxQueueLatency >= snapshot.LastQueueLatency);
    }


    [Fact]
    public async Task LatestRequiresSingleSlotAndDuplicateBranchIdsFailFast()
    {
        await using var router = new StreamRouter<int>();

        Assert.Throws<ArgumentException>(() => router.RegisterBranch(
            OptionalOptions("latest-invalid", capacity: 2, overflow: StreamOverflowPolicy.Latest),
            static (_, _) => ValueTask.CompletedTask));

        router.RegisterBranch(
            RequiredOptions("stable-id", capacity: 2),
            static (_, _) => ValueTask.CompletedTask);

        Assert.Throws<InvalidOperationException>(() => router.RegisterBranch(
            RequiredOptions("stable-id", capacity: 2),
            static (_, _) => ValueTask.CompletedTask));
    }

    [Fact]
    public async Task RequiredCancellationAfterEarlierRequiredAcceptanceFaultsRouter()
    {
        var firstValues = new ConcurrentQueue<int>();
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var router = new StreamRouter<int>();
        router.RegisterBranch(
            RequiredOptions("a", capacity: 8),
            (item, _) =>
            {
                firstValues.Enqueue(item.Value);
                return ValueTask.CompletedTask;
            });
        router.RegisterBranch(
            RequiredOptions("b", capacity: 1, overflow: StreamOverflowPolicy.Wait),
            async (item, token) =>
            {
                if (item.Value == 1)
                {
                    secondEntered.TrySetResult();
                    await secondRelease.Task.WaitAsync(token);
                }
            });

        router.Start();

        await router.PublishAsync(1);
        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await router.PublishAsync(2);

        using var cancellation = new CancellationTokenSource();
        var thirdTask = router.PublishAsync(3, cancellation.Token).AsTask();
        await Task.Delay(20);
        Assert.False(thirdTask.IsCompleted);
        cancellation.Cancel();

        var result = await thirdTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(StreamRouterState.Faulted, result.RouterState);
        Assert.True(result.IsPartialRequiredDelivery);
        Assert.Equal(
            StreamBranchPublishStatus.Accepted,
            Assert.Single(result.Branches, static branch => branch.BranchId == "a").Status);
        Assert.Equal(
            StreamBranchPublishStatus.Cancelled,
            Assert.Single(result.Branches, static branch => branch.BranchId == "b").Status);
        Assert.Contains(3, firstValues);

        secondRelease.TrySetResult();
        var terminal = await router.CompleteAsync(StreamCompletionMode.Drain);
        Assert.Equal(StreamRouterState.Faulted, terminal.State);
    }

    [Fact]
    public async Task DropNewestEvictsNewestBufferedItemAndReleasesItsOwnership()
    {
        var received = new ConcurrentQueue<int>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ownership = new TrackingOwnership();

        await using var router = new StreamRouter<TrackedItem>();
        router.RegisterBranch(
            OptionalOptions("ui", capacity: 2, overflow: StreamOverflowPolicy.DropNewest),
            async (item, token) =>
            {
                received.Enqueue(item.Value.Id);
                if (item.Value.Id == 1)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(token);
                }
            },
            ownership);

        router.Start();

        await router.PublishAsync(new TrackedItem(1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await router.PublishAsync(new TrackedItem(2));
        await router.PublishAsync(new TrackedItem(3));
        var fourth = await router.PublishAsync(new TrackedItem(4));

        var ui = Assert.Single(fourth.Branches);
        Assert.Equal(StreamBranchPublishStatus.Accepted, ui.Status);
        Assert.Equal(1, ui.DroppedCount);

        release.TrySetResult();
        await router.CompleteAsync(StreamCompletionMode.Drain);

        Assert.Equal(new[] { 1, 2, 4 }, received.ToArray());
        Assert.Contains(3, ownership.ReleasedIds);
        Assert.Equal(ownership.Retained, ownership.Released);
    }

    [Fact]
    public async Task PublishVsDisposeUnblocksRequiredWaitAndBalancesOwnership()
    {
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ownership = new TrackingOwnership();
            var router = new StreamRouter<TrackedItem>();

            router.RegisterBranch(
                RequiredOptions("required", capacity: 1, overflow: StreamOverflowPolicy.Wait),
                async (item, token) =>
                {
                    if (item.Value.Id == 1)
                    {
                        entered.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }
                },
                ownership);

            router.Start();
            await router.PublishAsync(new TrackedItem(1));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await router.PublishAsync(new TrackedItem(2));

            var blocked = router.PublishAsync(new TrackedItem(3)).AsTask();
            await WaitUntilAsync(() => ownership.Retained >= 3);
            Assert.False(blocked.IsCompleted);

            var dispose = router.DisposeAsync().AsTask();

            await Task.WhenAll(
                blocked.WaitAsync(TimeSpan.FromSeconds(2)),
                dispose.WaitAsync(TimeSpan.FromSeconds(2)));

            var result = await blocked;
            Assert.NotEqual(StreamBranchPublishStatus.Accepted, Assert.Single(result.Branches).Status);
            Assert.Equal(StreamRouterState.Disposed, result.RouterState);
            Assert.Equal(ownership.Retained, ownership.Released);
        }
    }

    [Fact]
    public async Task StreamRouterMetricsUseUnifiedMeterAndReturnQueueDepthToZero()
    {
        var measurements = new ConcurrentQueue<MetricMeasurement>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == OpenDeviceStudioTelemetry.InstrumentationName &&
                instrument.Name.StartsWith("opendevicestudio.streamrouter.", StringComparison.Ordinal))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            measurements.Enqueue(new MetricMeasurement(
                instrument.Name,
                measurement,
                tags.ToArray()));
        });
        listener.Start();

        var router = new StreamRouter<int>();
        router.RegisterBranch(
            RequiredOptions("metrics-required", capacity: 2),
            static (_, _) => ValueTask.CompletedTask);
        router.Start();

        await router.PublishAsync(1);
        await router.PublishAsync(2);
        await router.CompleteAsync(StreamCompletionMode.Drain);
        await router.DisposeAsync();

        var branchMeasurements = measurements
            .Where(static measurement =>
                measurement.Tags.Any(tag =>
                    tag.Key == "branch.id" &&
                    string.Equals(tag.Value?.ToString(), "metrics-required", StringComparison.Ordinal)))
            .ToArray();

        Assert.NotEmpty(branchMeasurements);
        Assert.Contains(
            branchMeasurements,
            measurement =>
                measurement.Instrument == "opendevicestudio.streamrouter.branch.capacity" &&
                measurement.Value == 2);

        var queueDepthDelta = branchMeasurements
            .Where(static measurement => measurement.Instrument == "opendevicestudio.streamrouter.branch.queue_depth")
            .Sum(static measurement => measurement.Value);
        Assert.Equal(0, queueDepthDelta);

        var activeDelta = branchMeasurements
            .Where(static measurement => measurement.Instrument == "opendevicestudio.streamrouter.active_branches")
            .Sum(static measurement => measurement.Value);
        Assert.Equal(0, activeDelta);

        Assert.All(branchMeasurements.SelectMany(static measurement => measurement.Tags), tag =>
        {
            Assert.DoesNotContain("subscription", tag.Key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sequence", tag.Key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("request", tag.Key, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task LongRunningRequiredAndLossyOptionalBranchesRemainBoundedAndLeakFree()
    {
        const int publishCount = 5000;
        var requiredDelivered = 0;
        var optionalEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var optionalRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ownership = new TrackingOwnership();

        await using var router = new StreamRouter<TrackedItem>();
        var required = router.RegisterBranch(
            RequiredOptions("required", capacity: 32),
            (_, _) =>
            {
                Interlocked.Increment(ref requiredDelivered);
                return ValueTask.CompletedTask;
            });
        var optional = router.RegisterBranch(
            OptionalOptions("optional", capacity: 4, overflow: StreamOverflowPolicy.DropOldest),
            async (item, token) =>
            {
                if (item.Value.Id == 0)
                {
                    optionalEntered.TrySetResult();
                    await optionalRelease.Task.WaitAsync(token);
                }
            },
            ownership);

        router.Start();

        await router.PublishAsync(new TrackedItem(0));
        await optionalEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        for (var value = 1; value < publishCount; value++)
        {
            var result = await router.PublishAsync(new TrackedItem(value));
            Assert.False(result.HasRequiredFailure);
            Assert.InRange(optional.GetSnapshot().QueueDepth, 0, 4);
        }

        optionalRelease.TrySetResult();
        await router.CompleteAsync(StreamCompletionMode.Drain);

        Assert.Equal(publishCount, Volatile.Read(ref requiredDelivered));
        Assert.Equal(0, required.GetSnapshot().QueueDepth);
        Assert.Equal(0, optional.GetSnapshot().QueueDepth);
        Assert.InRange(optional.GetSnapshot().HighWatermark, 0, 4);
        Assert.True(optional.GetSnapshot().Dropped > 0);
        Assert.Equal(ownership.Retained, ownership.Released);
        Assert.Equal(2, router.GetSnapshot().BranchCount);
    }

    private static StreamBranchOptions RequiredOptions(
        string id,
        int capacity,
        StreamOverflowPolicy overflow = StreamOverflowPolicy.Wait) =>
        new(
            id,
            id,
            capacity,
            StreamBranchDelivery.Required,
            overflow,
            StreamBranchFailurePolicy.Propagate,
            StreamOrderingPolicy.SerializedPublisherFifo);

    private static StreamBranchOptions OptionalOptions(
        string id,
        int capacity,
        StreamOverflowPolicy overflow) =>
        new(
            id,
            id,
            capacity,
            StreamBranchDelivery.Optional,
            overflow,
            StreamBranchFailurePolicy.Isolate,
            StreamOrderingPolicy.SerializedPublisherFifo);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
            await Task.Delay(1, timeout.Token);
    }

    private sealed record MetricMeasurement(
        string Instrument,
        long Value,
        KeyValuePair<string, object?>[] Tags);

    private sealed class TrackingOwnership : IStreamOwnershipAdapter<TrackedItem>
    {
        private int _retained;
        private int _released;
        private readonly ConcurrentQueue<int> _releasedIds = new();

        public int Retained => Volatile.Read(ref _retained);
        public int Released => Volatile.Read(ref _released);
        public IReadOnlyCollection<int> ReleasedIds => _releasedIds.ToArray();

        public TrackedItem Retain(TrackedItem item)
        {
            Interlocked.Increment(ref _retained);
            return item;
        }

        public void Release(TrackedItem item)
        {
            _releasedIds.Enqueue(item.Id);
            Interlocked.Increment(ref _released);
        }
    }

    private sealed record TrackedItem(int Id);
}
