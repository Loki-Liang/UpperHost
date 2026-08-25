using System.Runtime.CompilerServices;
using System.Threading.Channels;
using UpperHost.Abstractions.Transports;

namespace UpperHost.Transport.Simulator;

public sealed class SimulatorTransport : ITransport
{
    private readonly Channel<byte[]> _incoming;

    public SimulatorTransport(string name = "default", int capacity = 256)
    {
        Endpoint = new TransportEndpoint("simulator", name);
        _incoming = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
    }

    public event Action<ReadOnlyMemory<byte>>? Sent;

    public TransportEndpoint Endpoint { get; }
    public TransportState State { get; private set; } = TransportState.Closed;

    public Task OpenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        State = TransportState.Open;
        return Task.CompletedTask;
    }

    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        State = TransportState.Closed;
        return Task.CompletedTask;
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOpen();
        Sent?.Invoke(data.ToArray());
        return ValueTask.CompletedTask;
    }

    public ValueTask InjectAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        return _incoming.Writer.WriteAsync(data.ToArray(), cancellationToken);
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        await foreach (var item in _incoming.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    public ValueTask DisposeAsync()
    {
        _incoming.Writer.TryComplete();
        State = TransportState.Closed;
        return ValueTask.CompletedTask;
    }

    private void EnsureOpen()
    {
        if (State != TransportState.Open)
            throw new InvalidOperationException("Simulator transport is not open.");
    }
}
