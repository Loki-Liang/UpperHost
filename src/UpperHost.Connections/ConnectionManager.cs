using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using UpperHost.Abstractions.Connections;
using UpperHost.Abstractions.Observability;
using UpperHost.Abstractions.Transports;

namespace UpperHost.Connections;

public sealed class ConnectionManager : IConnectionManager
{
    private readonly ConcurrentDictionary<string, Lazy<Entry>> _entries =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<ConnectionManager>? _logger;
    private int _disposed;

    public ConnectionManager(ILogger<ConnectionManager>? logger = null) =>
        _logger = logger;

    public IReadOnlyCollection<ConnectionSnapshot> Connections =>
        _entries.Values
            .Where(entry => entry.IsValueCreated)
            .Select(entry => entry.Value.Snapshot())
            .OrderBy(snapshot => snapshot.Definition.Id.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public async ValueTask<IConnectionLease> AcquireAsync(
        ConnectionDefinition definition,
        ITransport transport,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(transport);

        var lazy = _entries.GetOrAdd(
            definition.Id.Value,
            _ => new Lazy<Entry>(
                () => new Entry(definition, transport),
                LazyThreadSafetyMode.ExecutionAndPublication));

        var entry = lazy.Value;
        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateCompatible(entry, definition, transport);

            if (definition.SharingMode == ConnectionSharingMode.Exclusive &&
                entry.LeaseCount > 0)
            {
                Record("acquire", entry, "rejected");
                throw new InvalidOperationException(
                    $"Connection '{definition.Id}' is exclusive and already leased.");
            }

            if (entry.LeaseCount == 0)
                await OpenAsync(entry, cancellationToken).ConfigureAwait(false);

            entry.LeaseCount++;
            UpperHostTelemetry.ActiveConnectionLeases.Add(
                1,
                UpperHostTelemetry.CreateMetricTags(MetricContext(entry, "lease", "acquired")));
            Record("acquire", entry, "success");

            _logger?.LogInformation(
                "Connection {ConnectionId} leased; mode={SharingMode}, leases={LeaseCount}, transport={Transport}",
                entry.Definition.Id.Value,
                entry.Definition.SharingMode,
                entry.LeaseCount,
                entry.Definition.Endpoint.Scheme);

            return new ConnectionLease(this, entry);
        }
        catch
        {
            if (entry.LeaseCount == 0 && entry.State == ConnectionState.Opening)
                entry.State = ConnectionState.Faulted;
            throw;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        List<Exception>? failures = null;

        foreach (var lazy in _entries.Values)
        {
            if (!lazy.IsValueCreated)
                continue;

            var entry = lazy.Value;
            await entry.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (entry.LeaseCount > 0)
                {
                    UpperHostTelemetry.ActiveConnectionLeases.Add(
                        -entry.LeaseCount,
                        UpperHostTelemetry.CreateMetricTags(LeaseMetricContext(entry)));
                    entry.LeaseCount = 0;
                }

                if (entry.State is ConnectionState.Open or
                    ConnectionState.Opening or
                    ConnectionState.Reconnecting or
                    ConnectionState.Faulted)
                {
                    try
                    {
                        await CloseAsync(entry, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        failures ??= [];
                        failures.Add(ex);
                    }
                }
            }
            finally
            {
                entry.Gate.Release();
                entry.Gate.Dispose();
            }
        }

        if (failures is not null)
            throw new AggregateException("One or more managed connections failed to close.", failures);
    }

    private async ValueTask ReleaseAsync(Entry entry)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        await entry.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (entry.LeaseCount <= 0)
                return;

            entry.LeaseCount--;
            UpperHostTelemetry.ActiveConnectionLeases.Add(
                -1,
                UpperHostTelemetry.CreateMetricTags(LeaseMetricContext(entry)));
            Record("release", entry, "success");

            _logger?.LogInformation(
                "Connection {ConnectionId} lease released; leases={LeaseCount}",
                entry.Definition.Id.Value,
                entry.LeaseCount);

            if (entry.LeaseCount == 0)
                await CloseAsync(entry, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private async Task OpenAsync(Entry entry, CancellationToken cancellationToken)
    {
        using var activity = StartActivity("open", entry);
        entry.State = ConnectionState.Opening;

        try
        {
            await entry.Transport.OpenAsync(cancellationToken).ConfigureAwait(false);
            entry.State = ConnectionState.Open;
            activity?.SetStatus(ActivityStatusCode.Ok);
            Record("open", entry, "success");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            entry.State = entry.Transport.State == TransportState.Closed
                ? ConnectionState.Closed
                : ConnectionState.Faulted;
            activity?.SetStatus(ActivityStatusCode.Unset);
            throw;
        }
        catch (Exception ex)
        {
            entry.State = ConnectionState.Faulted;
            MarkFailure(activity, ex, "connection_open_fault");
            Record("open", entry, "faulted", ex.GetType().FullName);
            _logger?.LogError(
                ex,
                "Connection {ConnectionId} failed to open using transport {Transport}",
                entry.Definition.Id.Value,
                entry.Definition.Endpoint.Scheme);
            throw;
        }
    }

    private async Task CloseAsync(Entry entry, CancellationToken cancellationToken)
    {
        using var activity = StartActivity("close", entry);
        entry.State = ConnectionState.Closing;

        try
        {
            await entry.Transport.CloseAsync(cancellationToken).ConfigureAwait(false);
            entry.State = ConnectionState.Closed;
            activity?.SetStatus(ActivityStatusCode.Ok);
            Record("close", entry, "success");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            entry.State = ConnectionState.Faulted;
            activity?.SetStatus(ActivityStatusCode.Unset);
            throw;
        }
        catch (Exception ex)
        {
            entry.State = ConnectionState.Faulted;
            MarkFailure(activity, ex, "connection_close_fault");
            Record("close", entry, "faulted", ex.GetType().FullName);
            _logger?.LogError(
                ex,
                "Connection {ConnectionId} failed to close using transport {Transport}",
                entry.Definition.Id.Value,
                entry.Definition.Endpoint.Scheme);
            throw;
        }
    }

    private static void ValidateCompatible(
        Entry entry,
        ConnectionDefinition definition,
        ITransport transport)
    {
        if (!Equals(entry.Definition.Endpoint, definition.Endpoint) ||
            entry.Definition.SharingMode != definition.SharingMode)
        {
            throw new InvalidOperationException(
                $"Connection '{definition.Id}' was already registered with a different definition.");
        }

        if (!ReferenceEquals(entry.Transport, transport))
        {
            throw new InvalidOperationException(
                $"Connection '{definition.Id}' was already bound to a different transport instance.");
        }
    }

    private static Activity? StartActivity(string operation, Entry entry) =>
        UpperHostTelemetry.StartActivity(
            $"upperhost.connection.{operation}",
            ActivityKind.Internal,
            new UpperHostTelemetryContext(
                ConnectionId: entry.Definition.Id.Value,
                Transport: entry.Definition.Endpoint.Scheme,
                Operation: operation));

    private static UpperHostMetricContext MetricContext(
        Entry entry,
        string operation,
        string outcome,
        string? errorType = null) =>
        new(
            Transport: entry.Definition.Endpoint.Scheme,
            Operation: operation,
            Outcome: outcome,
            ErrorType: errorType);

    private static UpperHostMetricContext LeaseMetricContext(Entry entry) =>
        new(
            Transport: entry.Definition.Endpoint.Scheme,
            Operation: "lease");

    private static void Record(
        string operation,
        Entry entry,
        string outcome,
        string? errorType = null)
    {
        var tags = UpperHostTelemetry.CreateMetricTags(
            MetricContext(entry, operation, outcome, errorType));
        UpperHostTelemetry.ConnectionOperations.Add(1, tags);
        if (errorType is not null)
            UpperHostTelemetry.ConnectionFailures.Add(1, tags);
    }

    private static void MarkFailure(Activity? activity, Exception exception, string errorCode)
    {
        activity?.SetTag("error.type", exception.GetType().FullName);
        activity?.SetTag("upperhost.error.code", errorCode);
        activity?.SetStatus(ActivityStatusCode.Error, errorCode);
    }

    private sealed class Entry
    {
        private int _state = (int)ConnectionState.Closed;
        private int _leaseCount;

        public Entry(ConnectionDefinition definition, ITransport transport)
        {
            Definition = definition;
            Transport = transport;
        }

        public ConnectionDefinition Definition { get; }
        public ITransport Transport { get; }
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public ConnectionState State
        {
            get => (ConnectionState)Volatile.Read(ref _state);
            set => Volatile.Write(ref _state, (int)value);
        }

        public int LeaseCount
        {
            get => Volatile.Read(ref _leaseCount);
            set => Volatile.Write(ref _leaseCount, value);
        }

        public ConnectionSnapshot Snapshot() =>
            new(Definition, State, LeaseCount);
    }

    private sealed class ConnectionLease : IConnectionLease
    {
        private readonly ConnectionManager _owner;
        private readonly Entry _entry;
        private readonly ITransport _view;
        private int _disposed;

        public ConnectionLease(ConnectionManager owner, Entry entry)
        {
            _owner = owner;
            _entry = entry;
            _view = new LeaseTransportView(this, entry, entry.Transport);
        }

        public ConnectionDefinition Definition => _entry.Definition;
        public ITransport Transport => _view;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            await _owner.ReleaseAsync(_entry).ConfigureAwait(false);
        }

        public void ThrowIfDisposed() =>
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private sealed class LeaseTransportView : ITransport
    {
        private readonly ConnectionLease _lease;
        private readonly Entry _entry;
        private readonly ITransport _inner;

        public LeaseTransportView(ConnectionLease lease, Entry entry, ITransport inner)
        {
            _lease = lease;
            _entry = entry;
            _inner = inner;
        }

        public TransportEndpoint Endpoint => _inner.Endpoint;
        public TransportState State => _inner.State;

        public Task OpenAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Connection lease transport lifecycle is owned by IConnectionManager.");

        public Task CloseAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Connection lease transport lifecycle is owned by IConnectionManager.");

        public async ValueTask SendAsync(
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default)
        {
            _lease.ThrowIfDisposed();

            try
            {
                await _inner.SendAsync(data, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                MarkRuntimeFault("send", ex);
                throw;
            }
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            _lease.ThrowIfDisposed();
            await using var enumerator = _inner
                .ReceiveAsync(cancellationToken)
                .GetAsyncEnumerator(cancellationToken);

            while (true)
            {
                bool hasItem;
                ReadOnlyMemory<byte> item = default;

                try
                {
                    hasItem = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    if (hasItem)
                        item = enumerator.Current;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    MarkRuntimeFault("receive", ex);
                    throw;
                }

                if (!hasItem)
                    yield break;

                _lease.ThrowIfDisposed();
                yield return item;
            }
        }

        private void MarkRuntimeFault(string operation, Exception exception)
        {
            _entry.State = ConnectionState.Faulted;
            Record(operation, _entry, "faulted", exception.GetType().FullName);

            using var activity = StartActivity(operation, _entry);
            MarkFailure(activity, exception, $"connection_{operation}_fault");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
