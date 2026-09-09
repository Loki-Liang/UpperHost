using UpperHost.Sample.DataAcquisition.Acquisition;

namespace UpperHost.Sample.DataAcquisition.Consumers;

public sealed record DisplayStatistics(int ReceivedFrames, long SequenceGaps);

public sealed class ConsoleDisplayConsumer
{
    private readonly TimeSpan _artificialRenderDelay;
    private readonly int _renderEvery;

    public ConsoleDisplayConsumer(TimeSpan artificialRenderDelay, int renderEvery)
    {
        if (artificialRenderDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(artificialRenderDelay));
        if (renderEvery <= 0) throw new ArgumentOutOfRangeException(nameof(renderEvery));

        _artificialRenderDelay = artificialRenderDelay;
        _renderEvery = renderEvery;
    }

    public async Task<DisplayStatistics> RunAsync(
        IAsyncEnumerable<SampleFrame> frames,
        CancellationToken cancellationToken = default)
    {
        var received = 0;
        long gaps = 0;
        long? previousSequence = null;

        await foreach (var frame in frames.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (previousSequence is long previous && frame.Sequence > previous + 1)
                gaps += frame.Sequence - previous - 1;

            previousSequence = frame.Sequence;
            received++;

            if (received % _renderEvery == 0)
            {
                var preview = string.Join(", ", frame.Channels.Select(value => value.ToString("0.000")));
                Console.WriteLine($"display seq={frame.Sequence,4} channels=[{preview}]");
            }

            if (_artificialRenderDelay > TimeSpan.Zero)
                await Task.Delay(_artificialRenderDelay, cancellationToken).ConfigureAwait(false);
        }

        return new DisplayStatistics(received, gaps);
    }
}
