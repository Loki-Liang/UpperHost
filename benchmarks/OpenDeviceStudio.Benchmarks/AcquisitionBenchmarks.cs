using BenchmarkDotNet.Attributes;
using OpenDeviceStudio.Abstractions.Storage;
using OpenDeviceStudio.Dataflow;

namespace OpenDeviceStudio.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 8)]
public class StreamRouterBenchmarks
{
    private StreamRouter<int> _router = null!;

    [GlobalSetup]
    public void Setup()
    {
        _router = new StreamRouter<int>();
        _router.RegisterBranch(
            new StreamBranchOptions(
                "required",
                "Required benchmark branch",
                Capacity: 4096,
                Delivery: StreamBranchDelivery.Required,
                Overflow: StreamOverflowPolicy.Wait,
                FailurePolicy: StreamBranchFailurePolicy.Propagate),
            static (_, _) => ValueTask.CompletedTask);
        _router.Start();
    }

    [Benchmark]
    public async Task PublishRequiredAsync()
    {
        var result = await _router.PublishAsync(42);
        if (result.HasRequiredFailure)
            throw new InvalidOperationException(result.RouterFault ?? "Required benchmark publish failed.");
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _router.CompleteAsync(StreamCompletionMode.Drain);
        await _router.DisposeAsync();
    }
}

[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 8)]
public class CanonicalRawBlockBenchmarks
{
    private CanonicalRawBlock _block = null!;

    [Params(256, 4096, 65536)]
    public int PayloadBytes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var payload = new byte[PayloadBytes];
        new Random(6201).NextBytes(payload);
        _block = CanonicalRawBlock.CopyFrom(
            sourceId: "benchmark",
            deviceId: "benchmark",
            connectionEpoch: 1,
            sequenceStart: 0,
            sequenceCount: 1,
            sampleCount: Math.Max(1, PayloadBytes / 2),
            channelCount: 1,
            channelLayoutId: "channel-0",
            numericRepresentation: RawNumericRepresentation.Int16,
            byteOrder: RawByteOrder.LittleEndian,
            sampleRateHz: 1000,
            deviceTimestamp: 0,
            hostMonotonicTimestamp: 0,
            wallClockTimestamp: DateTimeOffset.UnixEpoch,
            qualityFlags: 0,
            protocolVersion: "benchmark-v1",
            canonicalizationVersion: "raw-v1",
            configurationHash: "benchmark-cfg",
            payload: payload);
    }

    [Benchmark]
    public CanonicalRawBlock CloneOwned() => _block.CloneOwned();
}
