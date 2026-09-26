using OpenDeviceStudio.Abstractions.Transports;

namespace OpenDeviceStudio.Abstractions.Connections;

public sealed record ConnectionId
{
    public ConnectionId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Trim();
    }

    public string Value { get; }

    public override string ToString() => Value;

    public static ConnectionId FromEndpoint(TransportEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return new ConnectionId($"{endpoint.Scheme.Trim().ToLowerInvariant()}:{endpoint.Address.Trim()}");
    }
}

public enum ConnectionSharingMode
{
    Exclusive,
    Shared
}

public enum ConnectionState
{
    Closed,
    Opening,
    Open,
    Faulted,
    Reconnecting,
    Closing
}

public sealed record ConnectionDefinition
{
    public ConnectionDefinition(
        ConnectionId id,
        TransportEndpoint endpoint,
        ConnectionSharingMode sharingMode = ConnectionSharingMode.Exclusive)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(endpoint);

        Id = id;
        Endpoint = endpoint;
        SharingMode = sharingMode;
    }

    public ConnectionId Id { get; }
    public TransportEndpoint Endpoint { get; }
    public ConnectionSharingMode SharingMode { get; }

    public static ConnectionDefinition FromEndpoint(
        TransportEndpoint endpoint,
        ConnectionSharingMode sharingMode = ConnectionSharingMode.Exclusive) =>
        new(ConnectionId.FromEndpoint(endpoint), endpoint, sharingMode);
}

public sealed record ConnectionSnapshot(
    ConnectionDefinition Definition,
    ConnectionState State,
    int LeaseCount);

public interface IConnectionLease : IAsyncDisposable
{
    ConnectionDefinition Definition { get; }
    ITransport Transport { get; }
}

public interface IConnectionManager : IAsyncDisposable
{
    IReadOnlyCollection<ConnectionSnapshot> Connections { get; }

    ValueTask<IConnectionLease> AcquireAsync(
        ConnectionDefinition definition,
        ITransport transport,
        CancellationToken cancellationToken = default);
}
