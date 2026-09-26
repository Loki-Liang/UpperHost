using UpperHost.Dataflow;

namespace UpperHost.Tests;

public sealed class StreamRouterTests
{
    [Fact]
    public async Task RequiredBranchRejectsLossyOverflowPolicy()
    {
        await using var router = new StreamRouter<int>();

        Assert.Throws<ArgumentException>(() => router.RegisterBranch(new StreamBranchOptions(
            "raw",
            Capacity: 8,
            Delivery: StreamBranchDelivery.Required,
            Overflow: StreamOverflowPolicy.DropOldest,
            FailurePolicy: StreamBranchFailurePolicy.Propagate)));
    }

    [Fact]
    public async Task BranchesUseIndependentBoundedOverflowPolicies()
    {
        await using var router = new StreamRouter<int>();
        await using var required = router.RegisterBranch(new StreamBranchOptions(
            "required-processing",
            Capacity: 2,
            Delivery: StreamBranchDelivery.Required,
            Overflow: StreamOverflowPolicy.Wait,
            FailurePolicy: StreamBranchFailurePolicy.Propagate));
        await using var presentation = router.RegisterBranch(new StreamBranchOptions(
            "presentation",
            Capacity: 2,
            Delivery: StreamBranchDelivery.Optional,
            Overflow: StreamOverflowPolicy.DropOldest,
            FailurePolicy: StreamBranchFailurePolicy.Isolate));

        router.SealRequiredTopology();

        await router.PublishAsync(1);
        await router.PublishAsync(2);
        Assert.Equal(1, await required.ReadAsync());
        await router.PublishAsync(3);

        Assert.Equal(2, await required.ReadAsync());
        Assert.Equal(3, await required.ReadAsync());

        Assert.Equal(2, await presentation.ReadAsync());
        Assert.Equal(3, await presentation.ReadAsync());

        var snapshots = router.GetSnapshots();
        var requiredSnapshot = Assert.Single(snapshots, static snapshot => snapshot.Name == "required-processing");
        var presentationSnapshot = Assert.Single(snapshots, static snapshot => snapshot.Name == "presentation");

        Assert.Equal(0, requiredSnapshot.Dropped);
        Assert.Equal(1, presentationSnapshot.Dropped);
        Assert.InRange(requiredSnapshot.HighWatermark, 1, requiredSnapshot.Capacity);
        Assert.InRange(presentationSnapshot.HighWatermark, 1, presentationSnapshot.Capacity);
    }

    [Fact]
    public async Task RequiredRejectOverflowFailsInsteadOfGrowingUnbounded()
    {
        await using var router = new StreamRouter<int>();
        await using var branch = router.RegisterBranch(new StreamBranchOptions(
            "required",
            Capacity: 1,
            Delivery: StreamBranchDelivery.Required,
            Overflow: StreamOverflowPolicy.Reject,
            FailurePolicy: StreamBranchFailurePolicy.Propagate));

        router.SealRequiredTopology();
        await router.PublishAsync(1);

        await Assert.ThrowsAsync<StreamBranchOverflowException>(
            async () => await router.PublishAsync(2).AsTask());

        var snapshot = branch.GetSnapshot();
        Assert.Equal(1, snapshot.QueueDepth);
        Assert.Equal(1, snapshot.HighWatermark);
        Assert.Equal(1, snapshot.Rejected);
    }

    [Fact]
    public async Task OptionalBranchFailureIsIsolatedButRequiredFailurePropagates()
    {
        await using var router = new StreamRouter<int>();
        await using var required = router.RegisterBranch(new StreamBranchOptions(
            "raw-recorder",
            Capacity: 4,
            Delivery: StreamBranchDelivery.Required,
            Overflow: StreamOverflowPolicy.Wait,
            FailurePolicy: StreamBranchFailurePolicy.Propagate));
        await using var optional = router.RegisterBranch(new StreamBranchOptions(
            "ui",
            Capacity: 1,
            Delivery: StreamBranchDelivery.Optional,
            Overflow: StreamOverflowPolicy.Latest,
            FailurePolicy: StreamBranchFailurePolicy.Isolate));

        router.SealRequiredTopology();

        optional.ReportFailure(new InvalidOperationException("render failed"));
        await router.PublishAsync(7);

        Assert.Equal(7, await required.ReadAsync());
        Assert.True(optional.GetSnapshot().IsFaulted);

        required.ReportFailure(new IOException("disk failed"));

        var exception = await Assert.ThrowsAsync<StreamBranchFaultException>(
            async () => await router.PublishAsync(8).AsTask());

        Assert.Equal("disk failed", exception.InnerException?.Message);
    }

    [Fact]
    public async Task SealedRequiredTopologyStillAllowsOptionalAttachDetach()
    {
        await using var router = new StreamRouter<int>();
        await using var required = router.RegisterBranch(new StreamBranchOptions(
            "processing",
            Capacity: 2,
            Delivery: StreamBranchDelivery.Required,
            Overflow: StreamOverflowPolicy.Wait,
            FailurePolicy: StreamBranchFailurePolicy.Propagate));

        router.SealRequiredTopology();

        Assert.Throws<InvalidOperationException>(() => router.RegisterBranch(new StreamBranchOptions(
            "late-required",
            Capacity: 2,
            Delivery: StreamBranchDelivery.Required,
            Overflow: StreamOverflowPolicy.Wait,
            FailurePolicy: StreamBranchFailurePolicy.Propagate)));

        await using (var optional = router.RegisterBranch(new StreamBranchOptions(
                         "late-ui",
                         Capacity: 1,
                         Delivery: StreamBranchDelivery.Optional,
                         Overflow: StreamOverflowPolicy.Latest,
                         FailurePolicy: StreamBranchFailurePolicy.Isolate)))
        {
            Assert.Equal(2, router.BranchCount);
        }

        Assert.Equal(1, router.BranchCount);
    }

    [Fact]
    public async Task LatestPolicyCoalescesBacklogToNewestItem()
    {
        await using var router = new StreamRouter<int>();
        await using var branch = router.RegisterBranch(new StreamBranchOptions(
            "trend",
            Capacity: 8,
            Delivery: StreamBranchDelivery.Optional,
            Overflow: StreamOverflowPolicy.Latest,
            FailurePolicy: StreamBranchFailurePolicy.Isolate));

        await router.PublishAsync(1);
        await router.PublishAsync(2);
        await router.PublishAsync(3);

        Assert.Equal(1, branch.GetSnapshot().QueueDepth);
        Assert.Equal(3, await branch.ReadAsync());
        Assert.Equal(2, branch.GetSnapshot().Dropped);
    }

    [Fact]
    public async Task CopyPerBranchRequiresClonerAndReleasesDroppedCopies()
    {
        await using var invalidRouter = new StreamRouter<OwnedValue>();

        Assert.Throws<ArgumentException>(() => invalidRouter.RegisterBranch(new StreamBranchOptions(
            "copy",
            Capacity: 1,
            Ownership: StreamOwnershipPolicy.CopyPerBranch)));

        var released = new List<int>();
        await using var router = new StreamRouter<OwnedValue>(
            static value => new OwnedValue(value.Value),
            value => released.Add(value.Value));
        await using var branch = router.RegisterBranch(new StreamBranchOptions(
            "copy",
            Capacity: 1,
            Delivery: StreamBranchDelivery.Optional,
            Overflow: StreamOverflowPolicy.DropNewest,
            FailurePolicy: StreamBranchFailurePolicy.Isolate,
            Ownership: StreamOwnershipPolicy.CopyPerBranch));

        await router.PublishAsync(new OwnedValue(1));
        await router.PublishAsync(new OwnedValue(2));

        Assert.Equal([2], released);
        Assert.Equal(1, (await branch.ReadAsync()).Value);
    }

    private sealed record OwnedValue(int Value);
}
