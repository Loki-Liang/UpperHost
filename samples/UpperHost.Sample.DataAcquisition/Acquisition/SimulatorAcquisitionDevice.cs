using System.Runtime.CompilerServices;
using UpperHost.Abstractions.Data;
using UpperHost.Abstractions.Devices;

namespace UpperHost.Sample.DataAcquisition.Acquisition;

public sealed class SimulatorAcquisitionDevice : IDevice, IConnectable, IDataSource<SampleFrame>
{
    private readonly int _channelCount;
    private readonly int _frameCount;
    private readonly TimeSpan _samplePeriod;

    public SimulatorAcquisitionDevice(int channelCount, int frameCount, TimeSpan samplePeriod)
    {
        if (channelCount <= 0) throw new ArgumentOutOfRangeException(nameof(channelCount));
        if (frameCount <= 0) throw new ArgumentOutOfRangeException(nameof(frameCount));
        if (samplePeriod < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(samplePeriod));

        _channelCount = channelCount;
        _frameCount = frameCount;
        _samplePeriod = samplePeriod;
    }

    public DeviceDescriptor Descriptor { get; } = new(
        "sim-daq-1",
        "Simulated Multi-Channel DAQ",
        "UpperHost",
        "DAQ-SIM",
        "SIM-DAQ-0001");

    public DeviceState State { get; private set; } = DeviceState.Offline;

    public IReadOnlyCollection<string> Capabilities { get; } = ["connect", "streaming", "data-source"];

    public int ProducedFrames { get; private set; }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        State = DeviceState.Online;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        State = DeviceState.Offline;
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<SampleFrame> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (State != DeviceState.Online)
            throw new InvalidOperationException("Acquisition device must be Online before reading.");

        ProducedFrames = 0;

        for (var sequence = 0; sequence < _frameCount; sequence++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var channels = new double[_channelCount];
            for (var channel = 0; channel < channels.Length; channel++)
            {
                var phase = sequence * 0.05 + channel * 0.4;
                channels[channel] = Math.Sin(phase) + 0.1 * Math.Sin(phase * 7);
            }

            ProducedFrames++;
            yield return new SampleFrame(sequence, DateTimeOffset.UtcNow, channels);

            if (_samplePeriod > TimeSpan.Zero)
                await Task.Delay(_samplePeriod, cancellationToken).ConfigureAwait(false);
        }
    }
}
