using NWaves.Filters;
using OpenDeviceStudio.SignalProcessing;

namespace OpenDeviceStudio.SignalProcessing.NWaves;

public sealed class NwavesMovingAverageStageFactory : ISignalStageFactory<float>
{
    public NwavesMovingAverageStageFactory(
        string stageId,
        int windowLength,
        SignalGapPolicy gapPolicy = SignalGapPolicy.ResetAndMarkQuality)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageId);
        if (windowLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(windowLength));

        StageId = stageId;
        WindowLength = windowLength;
        GapPolicy = gapPolicy;
        ConfigurationHash = SignalConfigurationHash.Compute(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["backend"] = "NWaves",
                ["backendVersion"] = "0.9.6",
                ["filter"] = "MovingAverageFilter",
                ["windowLength"] = windowLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["gapPolicy"] = gapPolicy.ToString()
            });
    }

    public string StageId { get; }
    public int WindowLength { get; }
    public string Version => "NWaves-0.9.6/moving-average-v1";
    public string ConfigurationHash { get; }
    public bool IsStateful => true;
    public bool RequiresContinuity => true;
    public bool RequiresOrderedInput => true;
    public SignalGapPolicy GapPolicy { get; }
    public TimeSpan AlgorithmicDelay => TimeSpan.Zero;
    public SignalWindowContract? Window => null;

    public void ValidateInput(SignalDescriptor inputDescriptor)
    {
        ArgumentNullException.ThrowIfNull(inputDescriptor);
        inputDescriptor.Validate();

        if (inputDescriptor.SampleFormat != SignalSampleFormat.Float32)
        {
            throw new ArgumentException(
                "NWaves MovingAverage reference stage requires Float32 input.");
        }
    }

    public SignalDescriptor DescribeOutput(SignalDescriptor inputDescriptor)
    {
        ValidateInput(inputDescriptor);
        return inputDescriptor;
    }

    public ISignalStage<float> Create(SignalStageInstanceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new Stage(context.OutputDescriptor.ChannelCount, WindowLength);
    }

    private sealed class Stage : ISignalStage<float>
    {
        private readonly MovingAverageFilter[] _filters;

        public Stage(int channelCount, int windowLength)
        {
            _filters = Enumerable.Range(0, channelCount)
                .Select(_ => new MovingAverageFilter(windowLength))
                .ToArray();
        }

        public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask<SignalStageResult<float>> ProcessAsync(
            SignalBlock<float> input,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var source = input.Samples.Span;
            var output = new float[source.Length];
            var channels = input.Descriptor.ChannelCount;

            for (var sample = 0; sample < input.SampleCount; sample++)
            {
                var offset = sample * channels;
                for (var channel = 0; channel < channels; channel++)
                    output[offset + channel] = _filters[channel].Process(source[offset + channel]);
            }

            return ValueTask.FromResult(
                SignalStageResult<float>.One(
                    SignalStageOutput<float>.CopyFromInput(
                        input,
                        input.Descriptor,
                        output)));
        }

        public ValueTask<SignalStageResult<float>> CompleteAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(SignalStageResult<float>.Empty);
        }

        public ValueTask ResetAsync(
            SignalResetReason reason,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var filter in _filters)
                filter.Reset();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
