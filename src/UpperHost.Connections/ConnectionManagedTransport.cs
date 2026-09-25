using System.Runtime.CompilerServices;
using UpperHost.Abstractions.Connections;
using UpperHost.Abstractions.Transports;

namespace UpperHost.Connections;

public sealed class ConnectionManagedTransport : ITransport
{
    private readonly IConnectionManager _manager;
    private readonly ConnectionDefinition _definition;
    private readonly ITransport _inner;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private IConnectionLease? _lease;
    private int _disposed;

    public ConnectionManagedTransport(
        IConnectionManager manager,
        ConnectionDefinition definition,
        ITransport inner)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

        if (!Equals(definition.Endpoint, inner.Endpoint))
        {
            throw new ArgumentException(
                "Connection definition endpoint must match the transport endpoint.",
                nameof(definition));
        }
    }

    public TransportEndpoint Endpoint => _definition.Endpoint;
    public TransportState State => _inner.State;

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lease is not null)
                return;

            _lease = await _manager
                .AcquireAsync(_definition, _inner, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var lease = Interlocked.Exchange(ref _lease, null);
            if (lease is not null)
                await lease.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public ValueTask SendAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default) =>
        RequireLease().Transport.SendAsync(data, cancellationToken);

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var lease = RequireLease();
        await foreach (var item in lease.Transport.ReceiveAsync(cancellationToken).ConfigureAwait(false))
            yield return item;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var lease = Interlocked.Exchange(ref _lease, null);
            if (lease is not null)
                await lease.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
            _lifecycleGate.Dispose();
        }
    }

    private IConnectionLease RequireLease() =>
        Volatile.Read(ref _lease) ??
        throw new InvalidOperationException("Managed transport is not open.");
}
