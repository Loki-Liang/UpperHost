using System.Runtime.CompilerServices;
using UpperHost.Abstractions.Transports;

namespace UpperHost.Transport.VendorSdk;

public sealed record VendorSdkTransportOptions(
    string VendorName,
    string DeviceAddress,
    int ReadBufferSize = 16 * 1024)
{
    public string Address => $"{VendorName}/{DeviceAddress}";
}

public interface IVendorByteSession : IAsyncDisposable
{
    Task OpenAsync(VendorSdkTransportOptions options, CancellationToken cancellationToken = default);
    Task CloseAsync(CancellationToken cancellationToken = default);
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);
}

public sealed class VendorSdkTransport : ITransport
{
    private readonly IVendorByteSession _session;
    private readonly VendorSdkTransportOptions _options;

    public VendorSdkTransport(IVendorByteSession session, VendorSdkTransportOptions options)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(options.VendorName))
            throw new ArgumentException("Vendor name is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.DeviceAddress))
            throw new ArgumentException("Device address/id is required.", nameof(options));
        if (options.ReadBufferSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "ReadBufferSize must be greater than zero.");

        Endpoint = new TransportEndpoint("vendor-sdk", options.Address);
    }

    public TransportEndpoint Endpoint { get; }

    public TransportState State { get; private set; } = TransportState.Closed;

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (State == TransportState.Open)
            return;

        State = TransportState.Opening;
        try
        {
            await _session.OpenAsync(_options, cancellationToken).ConfigureAwait(false);
            State = TransportState.Open;
        }
        catch
        {
            State = TransportState.Faulted;
            throw;
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _session.CloseAsync(cancellationToken).ConfigureAwait(false);
        State = TransportState.Closed;
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        return _session.WriteAsync(data, cancellationToken);
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        var buffer = new byte[_options.ReadBufferSize];

        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await _session.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                yield break;

            yield return buffer.AsMemory(0, read).ToArray();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (State != TransportState.Closed)
            await CloseAsync().ConfigureAwait(false);

        await _session.DisposeAsync().ConfigureAwait(false);
    }

    private void EnsureOpen()
    {
        if (State != TransportState.Open)
            throw new InvalidOperationException("Vendor SDK transport is not open.");
    }
}
