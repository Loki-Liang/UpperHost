namespace UpperHost.Abstractions.Transports;

public enum TransportState
{
    Closed,
    Opening,
    Open,
    Faulted
}

public sealed record TransportEndpoint(string Scheme, string Address);

public interface ITransport : IAsyncDisposable
{
    TransportEndpoint Endpoint { get; }
    TransportState State { get; }

    Task OpenAsync(CancellationToken cancellationToken = default);
    Task CloseAsync(CancellationToken cancellationToken = default);
    ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default);
}
