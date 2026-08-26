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

public sealed class SerialTransport : ITransport
{
    private readonly SerialTransportOptions _options;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private SerialPort? _port;

    public SerialTransport(SerialTransportOptions options)
    {
        _options = options;
        Endpoint = new TransportEndpoint("serial", options.PortName);
    }

    public TransportEndpoint Endpoint { get; }
    public TransportState State { get; private set; } = TransportState.Closed;

    public Task OpenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (State == TransportState.Open) return Task.CompletedTask;

        State = TransportState.Opening;
        try
        {
            _port = new SerialPort(_options.PortName, _options.BaudRate, _options.Parity, _options.DataBits, _options.StopBits)
            {
                ReadBufferSize = _options.ReadBufferSize
            };
            _port.Open();
            State = TransportState.Open;
            return Task.CompletedTask;
        }
        catch
        {
            State = TransportState.Faulted;
            _port?.Dispose();
            _port = null;
            throw;
        }
    }

    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _port?.Dispose();
        _port = null;
        State = TransportState.Closed;
        return Task.CompletedTask;
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var stream = GetOpenStream();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var stream = GetOpenStream();
        var buffer = new byte[_options.ReadBufferSize];

        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) yield break;
            yield return buffer.AsMemory(0, read).ToArray();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _writeGate.Dispose();
    }

    private Stream GetOpenStream() =>
        State == TransportState.Open && _port is not null
            ? _port.BaseStream
            : throw new InvalidOperationException("Serial transport is not open.");
}
