using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using UpperHost.Abstractions.Transports;

namespace UpperHost.Protocols;

/// <summary>
/// Incremental frame parser contract. Implementations may discard invalid/resync bytes by
/// slicing <paramref name="buffer"/>. Returning false means no complete frame is currently
/// available; the remaining buffer must begin at the first incomplete candidate frame.
/// </summary>
public interface IFrameParser<TFrame>
{
    bool TryParse(
        ref ReadOnlySequence<byte> buffer,
        [MaybeNullWhen(false)] out TFrame frame);
}

public sealed record PipelinedFrameReaderOptions(
    long MaxRetainedBytes = 64 * 1024,
    long PauseWriterThreshold = 64 * 1024,
    long ResumeWriterThreshold = 32 * 1024)
{
    internal void Validate()
    {
        if (MaxRetainedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxRetainedBytes));
        if (PauseWriterThreshold <= 0)
            throw new ArgumentOutOfRangeException(nameof(PauseWriterThreshold));
        if (ResumeWriterThreshold < 0 || ResumeWriterThreshold >= PauseWriterThreshold)
            throw new ArgumentOutOfRangeException(
                nameof(ResumeWriterThreshold),
                "ResumeWriterThreshold must be non-negative and less than PauseWriterThreshold.");
        if (MaxRetainedBytes > PauseWriterThreshold)
            throw new ArgumentException(
                "MaxRetainedBytes must not exceed PauseWriterThreshold.",
                nameof(MaxRetainedBytes));
    }
}

/// <summary>
/// Bridges the existing transport chunk seam into System.IO.Pipelines so byte-stream
/// framing is incremental and bounded. PipeReader/PipeWriter remain implementation details.
/// </summary>
public sealed class PipelinedFrameReader<TFrame>
{
    private readonly ITransport _transport;
    private readonly IFrameParser<TFrame> _parser;
    private readonly PipelinedFrameReaderOptions _options;
    private int _activeReader;

    public PipelinedFrameReader(
        ITransport transport,
        IFrameParser<TFrame> parser,
        PipelinedFrameReaderOptions? options = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _options = options ?? new PipelinedFrameReaderOptions();
        _options.Validate();
    }

    public async IAsyncEnumerable<TFrame> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _activeReader, 1, 0) != 0)
            throw new InvalidOperationException("Only one frame reader may consume this protocol stream at a time.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: _options.PauseWriterThreshold,
            resumeWriterThreshold: _options.ResumeWriterThreshold,
            useSynchronizationContext: false));
        var pump = PumpAsync(pipe.Writer, linked.Token);

        try
        {
            while (true)
            {
                var result = await pipe.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
                var input = result.Buffer;
                var remaining = input;

                while (true)
                {
                    var before = remaining.Length;
                    if (_parser.TryParse(ref remaining, out var frame))
                    {
                        yield return frame;
                        continue;
                    }

                    if (remaining.Length == before)
                        break;
                }

                if (remaining.Length > _options.MaxRetainedBytes)
                {
                    throw new InvalidDataException(
                        $"Protocol parser retained {remaining.Length} bytes, exceeding the configured maximum {_options.MaxRetainedBytes}.");
                }

                var consumed = remaining.IsEmpty ? input.End : remaining.Start;
                pipe.Reader.AdvanceTo(consumed, input.End);

                if (!result.IsCompleted)
                    continue;

                if (!remaining.IsEmpty)
                {
                    throw new InvalidDataException(
                        $"Transport completed with {remaining.Length} bytes of an incomplete protocol frame.");
                }

                break;
            }

            await pump.ConfigureAwait(false);
        }
        finally
        {
            linked.Cancel();
            await pipe.Reader.CompleteAsync().ConfigureAwait(false);
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
            }

            Interlocked.Exchange(ref _activeReader, 0);
        }
    }

    private async Task PumpAsync(PipeWriter writer, CancellationToken cancellationToken)
    {
        Exception? error = null;
        try
        {
            await foreach (var chunk in _transport.ReceiveAsync(cancellationToken).ConfigureAwait(false))
            {
                if (chunk.IsEmpty)
                    continue;

                await writer.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            error = ex;
            throw;
        }
        finally
        {
            await writer.CompleteAsync(error).ConfigureAwait(false);
        }
    }
}
