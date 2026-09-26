using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using UpperHost.Abstractions.Observability;
using UpperHost.Dataflow;

namespace UpperHost.Tests;

public sealed class StreamRouterTests
{
    [Fact]
    public async Task Configuration_rejects_invalid_qos_and_seals_topology()
    {
        await using var router = new StreamRouter<int>();

        Assert.Throws<ArgumentOutOfRangeException>(() => router.RegisterBranch(
            StreamBranchOptions.Optional("bad-capacity", "bad", capacity: 0),
            ConsumeNoop));
        Assert.Throws<ArgumentException>(() => router.RegisterBranch(
            StreamBranchOptions.Required(
                "required-lossy",
                "bad",
                overflow: StreamOverflowPolicy.DropOldest),
            ConsumeNoop));
        Assert.Throws<ArgumentException>(() => router.RegisterBranch(
            new StreamBranchOptions(
                "optional-wait",
                "bad",
                4,
                StreamBranchDelivery.Optional,
                StreamOverflowPolicy.Wait,
                StreamBranchFailurePolicy.Isolate),
            ConsumeNoop));
        Assert.Throws<ArgumentException>(() => router.RegisterBranch(
            StreamBranchOptions.Optional(
                "latest-capacity",
                "bad",
                capacity: 2,
                overflow: StreamOverflowPolicy.Latest),
            ConsumeNoop));

        router.RegisterBranch(
            StreamBranchOptions.Required("required", "required"),
            ConsumeNoop);
        Assert.Throws<ArgumentException>(() => router.RegisterBranch(
            StreamBranchOptions.Optional("required", "duplicate"),
            ConsumeNoop));

        await router.StartAsync();
        Assert.Throws<InvalidOperationException>(() => router.RegisterBranch(
            StreamBranchOptions.Optional("late", "late"),
            ConsumeNoop));

        await router.CompleteAsync();
        Assert.Equal(StreamRouterState.Completed, router.State);
    }

    [Fact]
    public async Task Optional_slow_branch_drops_without_blocking_required_delivery()
    {
        var required = new List<int>();
        var optional = new List<int>();
        var optionalStarted = NewSignal();
        var releaseOptional = NewSignal();

        await using var router = new StreamRouter<int>();
        router.RegisterBranch(
            StreamBranchOptions.Required("required", "required", capacity: 8),
            (item, _) =>
            {
                required.Add(item);
                return ValueTask.CompletedTask;
            });
        router.RegisterBranch(
            StreamBranchOptions.Optional(
                "presentation",
                "presentation",
                capacity: 1,
                overflow: StreamOverflowPolicy.DropOldest,
                shutdownPolicy: StreamShutdownPolicy.Drain),
            async (item, token) =>
            {
                optional.Add(item);
                if (item == 1)
                {
                    optionalStarted.TrySetResult();
                    await releaseOptional.Task.WaitAsync(token);
                }
            });
        await router.StartAsync();

        Assert.True((await router.PublishAsync(1)).IsSuccess);
        await optionalStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        for (var value = 2; value <= 50; value++)
            Assert.True((await router.PublishAsync(value)).IsSuccess);

        releaseOptional.TrySetResult();
        await router.CompleteAsync();

        Assert.Equal(Enumerable.Range(1, 50), required);
        Assert.Equal(1, optional[0]);
        Assert.Equal(50, optional[^1]);

        var branch = router.GetSnapshot().Branches.Single(item => item.BranchId == "presentation");
        Assert.True(branch.Dropped >= 48);
        Assert.True(branch.QueueHighWater <= branch.Capacity);
        Assert.Equal(0, branch.QueueDepth);
    }

    [Fact]
    public async Task Required_reject_after_partial_accept_faults_router_and_reports_partial_publish()
    {
        var blockedStarted = NewSignal();
        var fast = new List<int>();

        await using var router = new StreamRouter<int>();
        router.RegisterBranch(
            StreamBranchOptions.Required(
                "required-fast",
                "required-fast",
                capacity: 8,
                overflow: StreamOverflowPolicy.Fail),
            (item, _) =>
            {
                fast.Add(item);
                return ValueTask.CompletedTask;
            });
        router.RegisterBranch(
            StreamBranchOptions.Required(
                "required-blocked",
                "required-blocked",
                capacity: 1,
                overflow: StreamOverflowPolicy.Fail),
            async (item, token) =>
            {
                if (item == 1)
                {
                    blockedStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
            });
        await router.StartAsync();

        Assert.True((await router.PublishAsync(1)).IsSuccess);
        await blockedStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True((await router.PublishAsync(2)).IsSuccess);

        var result = await router.PublishAsync(3);

        Assert.Equal(StreamRouterState.Faulted, result.RouterState);
        Assert.True(result.RequiresStop);
        Assert.Equal(
            StreamBranchPublishStatus.Accepted,
            result.Branches.Single(item => item.BranchId == "required-fast").Status);
        Assert.Equal(
            StreamBranchPublishStatus.Rejected,
            result.Branches.Single(item => item.BranchId == "required-blocked").Status);
        Assert.Contains(3, fast);
    }

    [Fact]
    public async Task Optional_consumer_fault_is_isolated_and_required_branch_continues()
    {
        var required = new List<int>();
        var optionalFaulted = NewSignal();

        await using var router = new StreamRouter<int>();
        router.RegisterBranch(
            StreamBranchOptions.Required("required", "required", capacity: 4),
            (item, _) =>
            {
                required.Add(item);
                return ValueTask.CompletedTask;
            });
        router.RegisterBranch(
            StreamBranchOptions.Optional("optional", "optional", capacity: 2),
            (item, _) =>
            {
                optionalFaulted.TrySetResult();
                throw new IOException("Injected optional fault.");
            });
        await router.StartAsync();

        Assert.True((await router.PublishAsync(1)).IsSuccess);
        await optionalFaulted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() =>
            router.GetSnapshot().Branches.Single(branch => branch.BranchId == "optional").State ==
            StreamBranchState.Faulted);

        var second = await router.PublishAsync(2);
        Assert.True(second.IsSuccess);
        Assert.Equal(StreamRouterState.Running, second.RouterState);

        await router.CompleteAsync();
        Assert.Equal(new[] { 1, 2 }, required);
        Assert.Equal(
            StreamBranchState.Faulted,
            router.GetSnapshot().Branches.Single(branch => branch.BranchId == "optional").State);
    }

    [Fact]
    public async Task Required_consumer_fault_faults_router_and_releases_owned_backlog()
    {
        var ownership = new CountingOwnership();
        var consumerStarted = NewSignal();
        var releaseConsumer = NewSignal();

        await using var router = new StreamRouter<OwnedItem>(ownership);
        router.RegisterBranch(
            StreamBranchOptions.Required("required", "required", capacity: 8),
            async (item, token) =>
            {
                consumerStarted.TrySetResult();
                await releaseConsumer.Task.WaitAsync(token);
                throw new IOException("Injected required fault.");
            });
        await router.StartAsync();

        Assert.True((await router.PublishAsync(new OwnedItem(1))).IsSuccess);
        await consumerStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True((await router.PublishAsync(new OwnedItem(2))).IsSuccess);
        Assert.True((await router.PublishAsync(new OwnedItem(3))).IsSuccess);

        releaseConsumer.TrySetResult();
        await WaitUntilAsync(() => router.State == StreamRouterState.Faulted);
        await router.DisposeAsync();

        Assert.Equal(0, ownership.Balance);
        var branch = router.GetSnapshot().Branches.Single();
        Assert.True(branch.Abandoned >= 1);
        Assert.Equal(0, branch.QueueDepth);
    }

    [Theory]
    [InlineData(StreamOverflowPolicy.DropOldest, 2)]
    [InlineData(StreamOverflowPolicy.DropNewest, 2)]
    [InlineData(StreamOverflowPolicy.DropWrite, 2)]
    [InlineData(StreamOverflowPolicy.Latest, 1)]
    public async Task Lossy_overflow_releases_every_retained_item(
        StreamOverflowPolicy overflow,
        int capacity)
    {
        var ownership = new CountingOwnership();
        var started = NewSignal();
        var release = NewSignal();

        await using var router = new StreamRouter<OwnedItem>(ownership);
        router.RegisterBranch(
            StreamBranchOptions.Optional(
                "optional",
                "optional",
                capacity,
                overflow,
                shutdownPolicy: StreamShutdownPolicy.Drain),
            async (item, token) =>
            {
                if (item.Value == 1)
                {
                    started.TrySetResult();
                    await release.Task.WaitAsync(token);
                }
            });
        await router.StartAsync();

        Assert.True((await router.PublishAsync(new OwnedItem(1))).IsSuccess);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for (var value = 2; value <= 20; value++)
            Assert.True((await router.PublishAsync(new OwnedItem(value))).IsSuccess);

        release.TrySetResult();
        await router.CompleteAsync();

        Assert.Equal(0, ownership.Balance);
        var snapshot = router.GetSnapshot().Branches.Single();
        Assert.True(snapshot.Dropped > 0);
        Assert.True(snapshot.QueueHighWater <= capacity);
        Assert.Equal(0, snapshot.QueueDepth);
    }

    [Fact]
    public async Task Required_wait_cancellation_does_not_enqueue_later()
    {
        var received = new List<int>();
        var started = NewSignal();
        var release = NewSignal();

        await using var router = new StreamRouter<int>();
        router.RegisterBranch(
            StreamBranchOptions.Required("required", "required", capacity: 1),
            async (item, token) =>
            {
                received.Add(item);
                if (item == 1)
                {
                    started.TrySetResult();
                    await release.Task.WaitAsync(token);
                }
            });
        await router.StartAsync();

        Assert.True((await router.PublishAsync(1)).IsSuccess);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True((await router.PublishAsync(2)).IsSuccess);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var cancelled = await router.PublishAsync(3, cancellation.Token);
        Assert.Equal(StreamBranchPublishStatus.Cancelled, cancelled.Branches.Single().Status);
        Assert.Equal(StreamRouterState.Running, cancelled.RouterState);

        release.TrySetResult();
        await router.CompleteAsync();

        Assert.Equal(new[] { 1, 2 }, received);
    }

    [Fact]
    public async Task Complete_drain_delivers_all_accepted_required_items()
    {
        var received = new List<int>();
        await using var router = new StreamRouter<int>();
        router.RegisterBranch(
            StreamBranchOptions.Required("required", "required", capacity: 32),
            async (item, token) =>
            {
                await Task.Delay(1, token);
                received.Add(item);
            });
        await router.StartAsync();

        for (var value = 0; value < 100; value++)
            Assert.True((await router.PublishAsync(value)).IsSuccess);

        await router.CompleteAsync();

        Assert.Equal(Enumerable.Range(0, 100), received);
        var snapshot = router.GetSnapshot();
        Assert.Equal(StreamRouterState.Completed, snapshot.State);
        Assert.Equal(0, snapshot.Branches.Single().QueueDepth);
        Assert.Equal(100, snapshot.Branches.Single().Delivered);
    }

    [Fact]
    public async Task Concurrent_publish_is_serialized_to_the_same_order_for_all_required_branches()
    {
        var first = new List<int>();
        var second = new List<int>();

        await using var router = new StreamRouter<int>();
        router.RegisterBranch(
            StreamBranchOptions.Required("first", "first", capacity: 128),
            (item, _) =>
            {
                first.Add(item);
                return ValueTask.CompletedTask;
            });
        router.RegisterBranch(
            StreamBranchOptions.Required("second", "second", capacity: 128),
            (item, _) =>
            {
                second.Add(item);
                return ValueTask.CompletedTask;
            });
        await router.StartAsync();

        var publishes = Enumerable.Range(0, 100)
            .Select(value => router.PublishAsync(value).AsTask())
            .ToArray();
        var results = await Task.WhenAll(publishes);
        Assert.All(results, result => Assert.True(result.IsSuccess));

        await router.CompleteAsync();

        Assert.Equal(first, second);
        Assert.Equal(100, first.Count);
        Assert.Equal(100, first.Distinct().Count());
    }

    [Fact]
    public async Task Dataflow_metrics_use_only_stable_branch_qos_tags()
    {
        var measurements = new ConcurrentBag<KeyValuePair<string, object?>[]>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == UpperHostTelemetry.InstrumentationName &&
                instrument.Name.StartsWith("upperhost.dataflow.", StringComparison.Ordinal))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            measurements.Add(tags.ToArray());
        });
        listener.Start();

        await using var router = new StreamRouter<int>();
        router.RegisterBranch(
            StreamBranchOptions.Required("metrics-required", "metrics required", capacity: 2),
            ConsumeNoop);
        await router.StartAsync();
        Assert.True((await router.PublishAsync(1)).IsSuccess);
        await router.CompleteAsync();

        Assert.NotEmpty(measurements);
        Assert.All(measurements, tags =>
        {
            Assert.DoesNotContain(tags, tag =>
                tag.Key.Contains("sequence", StringComparison.OrdinalIgnoreCase) ||
                tag.Key.Contains("subscription", StringComparison.OrdinalIgnoreCase) ||
                tag.Key.Contains("request", StringComparison.OrdinalIgnoreCase));
        });
        Assert.Contains(
            measurements.SelectMany(static item => item),
            tag => tag.Key == "upperhost.dataflow.branch" &&
                   string.Equals(tag.Value?.ToString(), "metrics-required", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Long_running_lossy_branch_remains_bounded_and_releases_all_ownership()
    {
        var ownership = new CountingOwnership();
        var started = NewSignal();
        var release = NewSignal();

        await using var router = new StreamRouter<OwnedItem>(ownership);
        router.RegisterBranch(
            StreamBranchOptions.Optional(
                "bounded",
                "bounded",
                capacity: 4,
                overflow: StreamOverflowPolicy.DropOldest,
                shutdownPolicy: StreamShutdownPolicy.Drain),
            async (item, token) =>
            {
                if (item.Value == 0)
                {
                    started.TrySetResult();
                    await release.Task.WaitAsync(token);
                }
            });
        await router.StartAsync();

        Assert.True((await router.PublishAsync(new OwnedItem(0))).IsSuccess);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for (var value = 1; value <= 5000; value++)
            Assert.True((await router.PublishAsync(new OwnedItem(value))).IsSuccess);

        var duringBacklog = router.GetSnapshot().Branches.Single();
        Assert.True(duringBacklog.QueueDepth <= 4);
        Assert.True(duringBacklog.QueueHighWater <= 4);

        release.TrySetResult();
        await router.CompleteAsync();

        var completed = router.GetSnapshot().Branches.Single();
        Assert.Equal(0, completed.QueueDepth);
        Assert.Equal(0, ownership.Balance);
    }

    private static ValueTask ConsumeNoop(int item, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
            await Task.Delay(1, timeout.Token);
    }

    private sealed record OwnedItem(int Value);

    private sealed class CountingOwnership : IStreamItemOwnership<OwnedItem>
    {
        private long _retained;
        private long _released;

        public long Balance => Volatile.Read(ref _retained) - Volatile.Read(ref _released);

        public OwnedItem Retain(OwnedItem item)
        {
            Interlocked.Increment(ref _retained);
            return item;
        }

        public void Release(OwnedItem item) => Interlocked.Increment(ref _released);
    }
}
