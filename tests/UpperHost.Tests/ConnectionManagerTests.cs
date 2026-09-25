using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using UpperHost.Abstractions.Connections;
using UpperHost.Abstractions.Diagnostics;
using UpperHost.Abstractions.Observability;
using UpperHost.Abstractions.Transports;
using UpperHost.Connections;
using UpperHost.Hosting;
using UpperHost.Starters;

namespace UpperHost.Tests;

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
        var builder = UpperHostApplication.CreateBuilder().AddUpperHostDefaults();
        builder.AddUpperHostTransport(_ => raw);

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
            if (instrument.Meter.Name == UpperHostTelemetry.InstrumentationName)
                listener.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            var captured = tags.ToArray();
            if (!captured.Any(tag =>
                    tag.Key == "upperhost.transport" &&
                    Equals(tag.Value, endpoint.Scheme)))
            {
                return;
            }

            if (instrument.Name == "upperhost.connection.active_leases")
                Interlocked.Add(ref activeLeases, measurement);

            if (instrument.Name == "upperhost.connection.operations")
            {
                lock (metricTags)
                    metricTags.AddRange(captured);
            }
        });
        meterListener.Start();

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == UpperHostTelemetry.InstrumentationName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "upperhost.connection.open" &&
                    Equals(activity.GetTagItem("upperhost.connection.id"), connectionId))
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
        Assert.Equal(connectionId, acquireActivity!.GetTagItem("upperhost.connection.id"));

        lock (metricTags)
        {
            Assert.DoesNotContain(metricTags, tag =>
                tag.Key is "upperhost.connection.id" or "upperhost.session.id" or "upperhost.command.id");
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
            lease.Transport.SendAsync([1, 2, 3]).AsTask());

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

    private sealed class CountingTransport : ITransport
    {
        private int _openCount;
        private int _closeCount;
        private int _remainingOpenFailures;
        private readonly bool _failSend;
        private readonly bool _failReceive;

        public CountingTransport(
            int failOpenAttempts = 0,
            bool failSend = false,
            bool failReceive = false,
            TransportEndpoint? endpoint = null)
        {
            _remainingOpenFailures = failOpenAttempts;
            _failSend = failSend;
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
            if (_failSend)
                throw new InvalidOperationException("planned send failure");
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
