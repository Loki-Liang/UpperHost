using UpperHost.Abstractions.Protocols;
using UpperHost.Abstractions.Transports;

namespace UpperHost.Protocols;

public sealed class RequestResponseClient<TCommand, TResponse>
{
    private readonly ITransport _transport;
    private readonly ICommandEncoder<TCommand> _encoder;
    private readonly IMessageDecoder<TResponse> _decoder;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RequestResponseClient(
        ITransport transport,
        ICommandEncoder<TCommand> encoder,
        IMessageDecoder<TResponse> decoder)
    {
        _transport = transport;
        _encoder = encoder;
        _decoder = decoder;
    }

    public async Task<TResponse> ExecuteAsync(
        TCommand command,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        await _gate.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        try
        {
            await _transport.SendAsync(_encoder.Encode(command), timeoutCts.Token).ConfigureAwait(false);

            await foreach (var chunk in _transport.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false))
            {
                _decoder.Append(chunk.Span);
                while (_decoder.TryRead(out var response))
                    return response!;
            }

            throw new EndOfStreamException("Transport closed before a response was decoded.");
        }
        finally
        {
            _gate.Release();
        }
    }
}

public sealed class StreamingProtocolReader<TMessage>
{
    private readonly ITransport _transport;
    private readonly IMessageDecoder<TMessage> _decoder;

    public StreamingProtocolReader(ITransport transport, IMessageDecoder<TMessage> decoder)
    {
        _transport = transport;
        _decoder = decoder;
    }

    public async IAsyncEnumerable<TMessage> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var chunk in _transport.ReceiveAsync(cancellationToken).ConfigureAwait(false))
        {
            _decoder.Append(chunk.Span);
            while (_decoder.TryRead(out var message))
                yield return message!;
        }
    }
}
