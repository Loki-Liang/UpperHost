using UpperHost.Sample.DataAcquisition.Acquisition;

namespace UpperHost.Sample.DataAcquisition.Consumers;

public sealed record DisplayStatistics(int ReceivedFrames, long SequenceGaps);

public sealed class ConsoleDisplayConsumer
{
    private readonly TimeSpan _artificialRenderDelay;
    private readonly int _renderEvery;
    private int _received;
    private long _sequenceGaps;
    private long? _previousSequence;

    public ConsoleDisplayConsumer(TimeSpan artificialRenderDelay, int renderEvery)
    {
        if (artificialRenderDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(artificialRenderDelay));
        if (renderEvery <= 0)
            throw new ArgumentOutOfRangeException(nameof(renderEvery));

        _artificialRenderDelay = artificialRenderDelay;
        _renderEvery = renderEvery;
    }

    public DisplayStatistics Statistics => new(_received, _sequenceGaps);

    public async ValueTask ConsumeAsync(
        SampleFrame frame,
        CancellationToken cancellationToken = default)
    {
        if (_previousSequence is long previous && frame.Sequence > previous + 1)
            _sequenceGaps += frame.Sequence - previous - 1;

        _previousSequence = frame.Sequence;
        _received++;

        if (_received % _renderEvery == 0)
        {
            var preview = string.Join(", ", frame.Channels.Select(value => value.ToString("0.000")));
            Console.WriteLine($"display seq={frame.Sequence,4} channels=[{preview}]");
        }

        if (_artificialRenderDelay > TimeSpan.Zero)
            await Task.Delay(_artificialRenderDelay, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DisplayStatistics> RunAsync(
        IAsyncEnumerable<SampleFrame> frames,
        CancellationToken cancellationToken = default)
    {
        await foreach (var frame in frames.WithCancellation(cancellationToken).ConfigureAwait(false))
            await ConsumeAsync(frame, cancellationToken).ConfigureAwait(false);

        return Statistics;
    }
}
