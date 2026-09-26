using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using OpenDeviceStudio.Abstractions.Connections;
using OpenDeviceStudio.Abstractions.Diagnostics;
using OpenDeviceStudio.Abstractions.Observability;
using OpenDeviceStudio.Abstractions.Transports;
using OpenDeviceStudio.Connections;
using OpenDeviceStudio.Hosting;
using OpenDeviceStudio.Resilience;
using OpenDeviceStudio.Starters;

namespace OpenDeviceStudio.Tests;

public sealed class ConnectionManagerTests
{
    [Fact]
    public async Task Shared_connection_opens_once_and_closes_after_last_lease()
    {
        await using var manager = new ConnectionManager();
        await using var transport = new CountingTransport();
        var definition = ConnectionDefinition.FromEndpoint(
            transport.Endpoint,
            ConnectionSharingMode.Shared);

        var first = await manager.AcquireAsync(definition, transport);
        var second = await manager.AcquireAsync(definition, transport);

        Assert.Equal(1, transport.OpenCount);
        Assert.Equal(ConnectionState.Open, Assert.Single(manager.Connections).State);
        Assert.Equal(2, Assert.Single(manager.Connections).LeaseCount);

        await first.DisposeAsync();

        Assert.Equal(0, transport.CloseCount);
        Assert.Equal(1, Assert.Single(manager.Connections).LeaseCount);

        await second.DisposeAsync();

        Assert.Equal(1, transport.CloseCount);
        Assert.Equal(ConnectionState.Closed, Assert.Single(manager.Connections).State);
        Assert.Equal(0, Assert.Single(manager.Connections).LeaseCount);
    }

    [Fact]
    public async Task Exclusive_connection_rejects_second_lease()
    {
        await using var manager = new ConnectionManager();
        await using var transport = new CountingTransport();
        var definition = ConnectionDefinition.FromEndpoint(
            transport.Endpoint,
            ConnectionSharingMode.Exclusive);

        await using var first = await manager.AcquireAsync(definition, transport);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.AcquireAsync(definition, transport).AsTask());

        Assert.Contains("exclusive", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, transport.OpenCount);
        Assert.Equal(1, Assert.Single(manager.Connections).LeaseCount);
    }

    [Fact]
    public async Task Concurrent_shared_acquires_use_one_physical_open()
    {
        await using var manager = new ConnectionManager();
        await using var transport = new CountingTransport();
        var definition = ConnectionDefinition.FromEndpoint(
            transport.Endpoint,
            ConnectionSharingMode.Shared);

        var leases = await Task.WhenAll(
            Enumerable.Range(0, 16)
                .Select(_ => manager.AcquireAsync(definition, transport).AsTask()));

        Assert.Equal(1, transport.OpenCount);
        Assert.Equal(16, Assert.Single(manager.Connections).LeaseCount);

        await Task.WhenAll(leases.Select(async lease => await lease.DisposeAsync()));

        Assert.Equal(1, transport.CloseCount);
        Assert.Equal(0, Assert.Single(manager.Connections).LeaseCount);
    }

    [Fact]
    public async Task Failed_open_keeps_zero_leases_and_can_retry()
    {
        await using var manager = new ConnectionManager();
        await using var transport = new CountingTransport(failOpenAttempts: 1);
        var definition = ConnectionDefinition.FromEndpoint(
            transport.Endpoint,
            ConnectionSharingMode.Shared);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.AcquireAsync(definition, transport).AsTask());

        var faulted = Assert.Single(manager.Connections);
        Assert.Equal(ConnectionState.Faulted, faulted.State);
        Assert.Equal(0, faulted.LeaseCount);

        await using var lease = await manager.AcquireAsync(definition, transport);

        Assert.Equal(2, transport.OpenCount);
        Assert.Equal(ConnectionState.Open, Assert.Single(manager.Connections).State);
        Assert.Equal(1, Assert.Single(manager.Connections).LeaseCount);
    }

    [Fact]
    public async Task Connection_health_reports_faulted_open()
    {
        await using var manager = new ConnectionManager();
        await using var transport = new CountingTransport(failOpenAttempts: 1);
        var definition = ConnectionDefinition.FromEndpoint(
            transport.Endpoint,
            ConnectionSharingMode.Shared);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.AcquireAsync(definition, transport).AsTask());

        var report = await new ConnectionHealthProbe(manager).CheckAsync();

        Assert.Equal(HealthStatus.Unhealthy, report.Status);
        Assert.Equal(1, report.Data!["Faulted"]);
        Assert.Equal(0, report.Data["Leases"]);
    }

    [Fact]
    public async Task Starter_transport_lifecycle_is_owned_by_connection_manager()
    {
        var raw = new CountingTransport();
        var builder = OpenDeviceStudioApplication.CreateBuilder().AddOpenDeviceStudioDefaults();
        builder.AddOpenDeviceStudioTransport(_ => raw);

        await using var app = builder.Build();
        var transport = app.Services.GetRequiredService<ITransport>();
        var manager = app.Services.GetRequiredService<IConnectionManager>();

        await transport.OpenAsync();

        var open = Assert.Single(manager.Connections);
        Assert.Equal(ConnectionState.Open, open.State);
        Assert.Equal(1, open.LeaseCount);
        Assert.Equal(1, raw.OpenCount);

        await transport.CloseAsync();

        var closed = Assert.Single(manager.Connections);
        Assert.Equal(ConnectionState.Closed, closed.State);
        Assert.Equal(0, closed.LeaseCount);
        Assert.Equal(1, raw.CloseCount);
    }

    [Fact]
    public async Task Connection_activity_keeps_id_out_of_metric_tags_and_lease_series_balances()
    {
        var connectionId = $"connection-{Guid.NewGuid():N}";
        var endpoint = new TransportEndpoint("connection-test", connectionId);
        var definition = new ConnectionDefinition(
            new ConnectionId(connectionId),
            endpoint,
            ConnectionSharingMode.Shared);

        long activeLeases = 0;
        var metricTags = new List<KeyValuePair<string, object?>>();
        Activity? acquireActivity = null;

        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == OpenDeviceStudioTelemetry.InstrumentationName)
                listener.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            var captured = tags.ToArray();
            if (!captured.Any(tag =>
                    tag.Key == "opendevicestudio.transport" &&
                    Equals(tag.Value, endpoint.Scheme)))
            {
                return;
            }

            if (instrument.Name == "opendevicestudio.connection.active_leases")
                Interlocked.Add(ref activeLeases, measurement);

            if (instrument.Name == "opendevicestudio.connection.operations")
            {
                lock (metricTags)
                    metricTags.AddRange(captured);
            }
        });
        meterListener.Start();

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == OpenDeviceStudioTelemetry.InstrumentationName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "opendevicestudio.connection.open" &&
                    Equals(activity.GetTagItem("opendevicestudio.connection.id"), connectionId))
                {
                    acquireActivity = activity;
                }
            }
        };
        ActivitySource.AddActivityListener(activityListener);

        await using var manager = new ConnectionManager();
        await using var transport = new CountingTransport(endpoint: endpoint);

        var lease = await manager.AcquireAsync(definition, transport);
        await lease.DisposeAsync();

        Assert.Equal(0, Volatile.Read(ref activeLeases));
        Assert.NotNull(acquireActivity);
        Assert.Equal(connectionId, acquireActivity!.GetTagItem("opendevicestudio.connection.id"));

        lock (metricTags)
        {
            Assert.DoesNotContain(metricTags, tag =>
                tag.Key is "opendevicestudio.connection.id" or "opendevicestudio.session.id" or "opendevicestudio.command.id");
        }
    }

    [Fact]
    public async Task Send_failure_marks_connection_faulted_and_health_unhealthy()
    {
        await using var manager = new ConnectionManager();
        await using var transport = new CountingTransport(failSend: true);
        var definition = ConnectionDefinition.FromEndpoint(
            transport.Endpoint,
            ConnectionSharingMode.Shared);

        await using var lease = await manager.AcquireAsync(definition, transport);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lease.Transport.SendAsync(new byte[] { 1, 2, 3 }).AsTask());

        var snapshot = Assert.Single(manager.Connections);
        Assert.Equal(ConnectionState.Faulted, snapshot.State);
        Assert.Equal(1, snapshot.LeaseCount);

        var report = await new ConnectionHealthProbe(manager).CheckAsync();
        Assert.Equal(HealthStatus.Unhealthy, report.Status);
    }

    [Fact]
    public async Task Receive_failure_marks_connection_faulted()
    {
        await using var manager = new ConnectionManager();
        await using var transport = new CountingTransport(failReceive: true);
        var definition = ConnectionDefinition.FromEndpoint(
            transport.Endpoint,
            ConnectionSharingMode.Shared);

        await using var lease = await manager.AcquireAsync(definition, transport);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in lease.Transport.ReceiveAsync())
            {
            }
        });

        var snapshot = Assert.Single(manager.Connections);
        Assert.Equal(ConnectionState.Faulted, snapshot.State);
        Assert.Equal(1, snapshot.LeaseCount);
    }

    [Fact]
    public async Task Lease_transport_cannot_bypass_manager_lifecycle()
    {
        await using var manager = new ConnectionManager();
        await using var transport = new CountingTransport();
        var definition = ConnectionDefinition.FromEndpoint(
            transport.Endpoint,
            ConnectionSharingMode.Shared);

        await using var lease = await manager.AcquireAsync(definition, transport);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lease.Transport.CloseAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lease.Transport.OpenAsync());

        Assert.Equal(ConnectionState.Open, Assert.Single(manager.Connections).State);
        Assert.Equal(0, transport.CloseCount);
    }

    [Fact]
    public async Task Managed_transport_serializes_open_close_race()
    {
        await using var manager = new ConnectionManager();
        await using var raw = new CountingTransport();
        await using var managed = new ConnectionManagedTransport(
            manager,
            ConnectionDefinition.FromEndpoint(raw.Endpoint, ConnectionSharingMode.Shared),
            raw);

        var openTask = managed.OpenAsync();
        var closeTask = managed.CloseAsync();

        await Task.WhenAll(openTask, closeTask);

        var snapshot = Assert.Single(manager.Connections);
        Assert.Equal(ConnectionState.Closed, snapshot.State);
        Assert.Equal(0, snapshot.LeaseCount);
        Assert.Equal(1, raw.OpenCount);
        Assert.Equal(1, raw.CloseCount);
    }

    [Fact]
    public async Task Close_racing_with_inflight_send_failure_does_not_resurrect_faulted_state()
    {
        await using var manager = new ConnectionManager();
        await using var raw = new DeferredSendFailureTransport();
        var definition = ConnectionDefinition.FromEndpoint(
            raw.Endpoint,
            ConnectionSharingMode.Shared);
        var lease = await manager.AcquireAsync(definition, raw);

        var sendTask = lease.Transport.SendAsync(new byte[] { 1 }).AsTask();
        await raw.SendStarted;

        await lease.DisposeAsync();

        Assert.Equal(ConnectionState.Closed, Assert.Single(manager.Connections).State);
        raw.ReleaseSendFailure();

        await Assert.ThrowsAsync<InvalidOperationException>(() => sendTask);

        var snapshot = Assert.Single(manager.Connections);
        Assert.Equal(ConnectionState.Closed, snapshot.State);
        Assert.Equal(0, snapshot.LeaseCount);
        Assert.Equal(1, raw.CloseCount);
    }

    [Fact]
    public async Task Manager_shutdown_closes_active_connection_and_late_lease_dispose_is_safe()
    {
        var manager = new ConnectionManager();
        await using var raw = new CountingTransport();
        var definition = ConnectionDefinition.FromEndpoint(
            raw.Endpoint,
            ConnectionSharingMode.Shared);
        var lease = await manager.AcquireAsync(definition, raw);

        await manager.DisposeAsync();

        var snapshot = Assert.Single(manager.Connections);
        Assert.Equal(ConnectionState.Closed, snapshot.State);
        Assert.Equal(0, snapshot.LeaseCount);
        Assert.Equal(1, raw.CloseCount);

        await lease.DisposeAsync();
        Assert.Equal(1, raw.CloseCount);
    }

    [Fact]
    public async Task Reconnecting_transport_reopens_through_connection_manager()
    {
        await using var manager = new ConnectionManager();
        await using var raw = new CountingTransport(failSendAttempts: 1);
        var definition = ConnectionDefinition.FromEndpoint(
            raw.Endpoint,
            ConnectionSharingMode.Shared);
        await using var managed = new ConnectionManagedTransport(manager, definition, raw);
        await using var resilient = new ReconnectingTransport(
            managed,
            new ReconnectPolicy(
                MaxAttempts: 1,
                InitialDelay: TimeSpan.Zero,
                BackoffFactor: 1,
                MaximumDelay: TimeSpan.Zero));

        await resilient.OpenAsync();
        await resilient.SendAsync(new byte[] { 1 });

        var snapshot = Assert.Single(manager.Connections);
        Assert.Equal(ConnectionState.Open, snapshot.State);
        Assert.Equal(1, snapshot.LeaseCount);
        Assert.Equal(2, raw.OpenCount);
        Assert.Equal(1, raw.CloseCount);
    }

    [Fact]
    public async Task Cancellation_during_open_leaves_no_lease_or_physical_handle()
    {
        await using var manager = new ConnectionManager();
        await using var raw = new CancellableOpenTransport();
        var definition = ConnectionDefinition.FromEndpoint(
            raw.Endpoint,
            ConnectionSharingMode.Shared);
        using var cancellation = new CancellationTokenSource();

        var acquireTask = manager.AcquireAsync(
            definition,
            raw,
            cancellation.Token).AsTask();

        await raw.OpenStarted;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquireTask);

        var snapshot = Assert.Single(manager.Connections);
        Assert.Equal(ConnectionState.Closed, snapshot.State);
        Assert.Equal(0, snapshot.LeaseCount);
        Assert.Equal(TransportState.Closed, raw.State);
    }

    [Fact]
    public async Task Disposing_managed_consumer_releases_last_lease_and_closes_handle()
    {
        await using var manager = new ConnectionManager();
        await using var raw = new CountingTransport();
        var managed = new ConnectionManagedTransport(
            manager,
            ConnectionDefinition.FromEndpoint(raw.Endpoint, ConnectionSharingMode.Shared),
            raw);

        await managed.OpenAsync();
        await managed.DisposeAsync();

        var snapshot = Assert.Single(manager.Connections);
        Assert.Equal(ConnectionState.Closed, snapshot.State);
        Assert.Equal(0, snapshot.LeaseCount);
        Assert.Equal(1, raw.CloseCount);
    }

    private sealed class CancellableOpenTransport : ITransport
    {
        private readonly TaskCompletionSource<bool> _openStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TransportEndpoint Endpoint { get; } =
            new("cancellable-open", "default");

        public TransportState State { get; private set; } = TransportState.Closed;
        public Task OpenStarted => _openStarted.Task;

        public async Task OpenAsync(CancellationToken cancellationToken = default)
        {
            State = TransportState.Opening;
            _openStarted.TrySetResult(true);

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                State = TransportState.Closed;
                throw;
            }
        }

        public Task CloseAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            State = TransportState.Closed;
            return Task.CompletedTask;
        }

        public ValueTask SendAsync(
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("transport never reaches open state in this test");

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield break;
        }

        public ValueTask DisposeAsync()
        {
            State = TransportState.Closed;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DeferredSendFailureTransport : ITransport
    {
        private readonly TaskCompletionSource<bool> _sendStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseFailure =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _closeCount;

        public TransportEndpoint Endpoint { get; } =
            new("deferred-failure", "default");

        public TransportState State { get; private set; } = TransportState.Closed;
        public Task SendStarted => _sendStarted.Task;
        public int CloseCount => Volatile.Read(ref _closeCount);

        public Task OpenAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            State = TransportState.Open;
            return Task.CompletedTask;
        }

        public Task CloseAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _closeCount);
            State = TransportState.Closed;
            return Task.CompletedTask;
        }

        public async ValueTask SendAsync(
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _sendStarted.TrySetResult(true);
            await _releaseFailure.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("planned deferred send failure");
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield break;
        }

        public void ReleaseSendFailure() => _releaseFailure.TrySetResult(true);

        public ValueTask DisposeAsync()
        {
            State = TransportState.Closed;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingTransport : ITransport
    {
        private int _openCount;
        private int _closeCount;
        private int _remainingOpenFailures;
        private int _remainingSendFailures;
        private readonly bool _failReceive;

        public CountingTransport(
            int failOpenAttempts = 0,
            bool failSend = false,
            bool failReceive = false,
            int failSendAttempts = 0,
            TransportEndpoint? endpoint = null)
        {
            _remainingOpenFailures = failOpenAttempts;
            _remainingSendFailures = failSend ? int.MaxValue : failSendAttempts;
            _failReceive = failReceive;
            Endpoint = endpoint ?? new TransportEndpoint("counting", "default");
        }

        public TransportEndpoint Endpoint { get; }
        public TransportState State { get; private set; } = TransportState.Closed;
        public int OpenCount => Volatile.Read(ref _openCount);
        public int CloseCount => Volatile.Read(ref _closeCount);

        public async Task OpenAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _openCount);
            State = TransportState.Opening;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();

            if (Interlocked.Decrement(ref _remainingOpenFailures) >= 0)
            {
                State = TransportState.Faulted;
                throw new InvalidOperationException("planned open failure");
            }

            State = TransportState.Open;
        }

        public async Task CloseAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _closeCount);
            await Task.Yield();
            State = TransportState.Closed;
        }

        public ValueTask SendAsync(
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (State != TransportState.Open)
                throw new InvalidOperationException("transport is not open");
            if (Volatile.Read(ref _remainingSendFailures) > 0)
            {
                Interlocked.Decrement(ref _remainingSendFailures);
                throw new InvalidOperationException("planned send failure");
            }
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (State != TransportState.Open)
                throw new InvalidOperationException("transport is not open");
            await Task.Yield();
            if (_failReceive)
                throw new InvalidOperationException("planned receive failure");
            yield break;
        }

        public ValueTask DisposeAsync()
        {
            State = TransportState.Closed;
            return ValueTask.CompletedTask;
        }
    }
}
