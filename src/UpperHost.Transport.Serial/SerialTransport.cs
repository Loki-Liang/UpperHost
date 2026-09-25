using System.IO.Ports;
using System.Runtime.CompilerServices;
using UpperHost.Abstractions.Transports;

namespace UpperHost.Transport.Serial;

public sealed record SerialTransportOptions(
    string PortName,
    int BaudRate = 115200,
    int DataBits = 8,
    Parity Parity = Parity.None,
    StopBits StopBits = StopBits.One,
    int ReadBufferSize = 16 * 1024);

public interface ISerialByteChannel : IAsyncDisposable
{
    ValueTask OpenAsync(CancellationToken cancellationToken = default);
    ValueTask CloseAsync(CancellationToken cancellationToken = default);
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);
}

public sealed class SerialTransport : ITransport
{
    private readonly SerialTransportOptions _options;
    private readonly ISerialByteChannel _channel;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _disposed;

    public SerialTransport(SerialTransportOptions options)
        : this(options, new SystemSerialByteChannel(options))
    {
    }

    public SerialTransport(SerialTransportOptions options, ISerialByteChannel channel)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(channel);

        if (string.IsNullOrWhiteSpace(options.PortName))
            throw new ArgumentException("Serial port name is required.", nameof(options));
        if (options.ReadBufferSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "ReadBufferSize must be positive.");

        _options = options;
        _channel = channel;
        Endpoint = new TransportEndpoint("serial", options.PortName);
    }

    public TransportEndpoint Endpoint { get; }
    public TransportState State { get; private set; } = TransportState.Closed;

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (State == TransportState.Open)
            return;

        State = TransportState.Opening;
        try
        {
            await _channel.OpenAsync(cancellationToken).ConfigureAwait(false);
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
        ThrowIfDisposed();
        await _channel.CloseAsync(cancellationToken).ConfigureAwait(false);
        State = TransportState.Closed;
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureOpen();

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _channel.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureOpen();

        var buffer = new byte[_options.ReadBufferSize];
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await _channel.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                yield break;

            yield return buffer.AsMemory(0, read).ToArray();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            await _channel.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            State = TransportState.Closed;
            _writeGate.Dispose();
        }
    }

    private void EnsureOpen()
    {
        if (State != TransportState.Open)
            throw new InvalidOperationException("Serial transport is not open.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}

internal sealed class SystemSerialByteChannel : ISerialByteChannel
{
    private readonly SerialTransportOptions _options;
    private SerialPort? _port;

    public SystemSerialByteChannel(SerialTransportOptions options) => _options = options;

    public ValueTask OpenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_port?.IsOpen == true)
            return ValueTask.CompletedTask;

        _port?.Dispose();
        _port = new SerialPort(_options.PortName, _options.BaudRate, _options.Parity, _options.DataBits, _options.StopBits)
        {
            ReadBufferSize = _options.ReadBufferSize
        };
        _port.Open();
        return ValueTask.CompletedTask;
    }

    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _port?.Dispose();
        _port = null;
        return ValueTask.CompletedTask;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var stream = GetOpenStream();
        await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        GetOpenStream().ReadAsync(buffer, cancellationToken);

    public ValueTask DisposeAsync()
    {
        _port?.Dispose();
        _port = null;
        return ValueTask.CompletedTask;
    }

    private Stream GetOpenStream() =>
        _port?.IsOpen == true
            ? _port.BaseStream
            : throw new InvalidOperationException("Serial byte channel is not open.");
}
