using System.Runtime.CompilerServices;
using UpperHost.Abstractions.Transports;

namespace UpperHost.Transport.Usb;

public sealed record UsbTransportOptions(
    ushort VendorId,
    ushort ProductId,
    byte InterfaceNumber = 0,
    byte InEndpoint = 0x81,
    byte OutEndpoint = 0x01,
    int ReadBufferSize = 16 * 1024)
{
    public string Address => $"{VendorId:X4}:{ProductId:X4}/if{InterfaceNumber}";
}

public interface IUsbByteChannel : IAsyncDisposable
{
    Task OpenAsync(UsbTransportOptions options, CancellationToken cancellationToken = default);
    Task CloseAsync(CancellationToken cancellationToken = default);
    ValueTask WriteAsync(byte endpoint, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
    ValueTask<int> ReadAsync(byte endpoint, Memory<byte> buffer, CancellationToken cancellationToken = default);
}

public sealed class UsbTransport : ITransport
{
    private readonly IUsbByteChannel _channel;
    private readonly UsbTransportOptions _options;

    public UsbTransport(IUsbByteChannel channel, UsbTransportOptions options)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (options.ReadBufferSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "ReadBufferSize must be greater than zero.");

        Endpoint = new TransportEndpoint("usb", options.Address);
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
            await _channel.OpenAsync(_options, cancellationToken).ConfigureAwait(false);
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
        await _channel.CloseAsync(cancellationToken).ConfigureAwait(false);
        State = TransportState.Closed;
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        return _channel.WriteAsync(_options.OutEndpoint, data, cancellationToken);
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        var buffer = new byte[_options.ReadBufferSize];

        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await _channel.ReadAsync(_options.InEndpoint, buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                yield break;

            yield return buffer.AsMemory(0, read).ToArray();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (State != TransportState.Closed)
            await CloseAsync().ConfigureAwait(false);
        await _channel.DisposeAsync().ConfigureAwait(false);
    }

    private void EnsureOpen()
    {
        if (State != TransportState.Open)
            throw new InvalidOperationException("USB transport is not open.");
    }
}
