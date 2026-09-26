using System.Collections.Concurrent;
using OpenDeviceStudio.Abstractions.Storage;
using OpenDeviceStudio.Dataflow;
using OpenDeviceStudio.Storage.FileSystem;

namespace OpenDeviceStudio.Tests;

public sealed class AcquisitionVerificationTests
{
    [Fact]
    public async Task Slow_disk_backpressures_raw_without_silent_drop_and_stays_bounded()
    {
        using var temp = new TemporaryDirectory();
        var streamFactory = new GatedSegmentStreamFactory();
        await using var recorder = new FileSystemRawRecorder(
            Options(temp.Path, queueCapacity: 2),
            TimeProvider.System,
            streamFactory);

        await recorder.PrepareAsync(Descriptor("verification-slow-disk"));

        Assert.True((await recorder.AcceptAsync(Block(1))).Accepted);
        await streamFactory.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True((await recorder.AcceptAsync(Block(2))).Accepted);
        Assert.True((await recorder.AcceptAsync(Block(3))).Accepted);

        var blocked = recorder.AcceptAsync(Block(4)).AsTask();
        await Task.Yield();

        Assert.False(blocked.IsCompleted);
        Assert.Equal(RawRecorderState.Running, recorder.Snapshot.State);
        Assert.InRange(recorder.Snapshot.QueueDepth, 0, recorder.Snapshot.QueueCapacity);
        Assert.InRange(recorder.Snapshot.QueueHighWater, 0, recorder.Snapshot.QueueCapacity);
        Assert.Equal(3, recorder.Snapshot.AcceptedBlocks);

        streamFactory.ReleaseWrites.TrySetResult();
        Assert.True((await blocked.WaitAsync(TimeSpan.FromSeconds(5))).Accepted);

        await recorder.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await recorder.FinalizeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        var terminal = recorder.Snapshot;
        Assert.Equal(RawRecorderState.Completed, terminal.State);
        Assert.Equal(4, terminal.AcceptedBlocks);
        Assert.Equal(4, terminal.WrittenBlocks);
        Assert.Equal(0, terminal.SequenceGapCount);
        Assert.Equal(0, terminal.DuplicateBlockCount);
        Assert.Equal(0, terminal.OutOfOrderBlockCount);
        Assert.Equal(0, terminal.QueueDepth);
        Assert.InRange(terminal.QueueHighWater, 0, terminal.QueueCapacity);

        var recovery = await FileSystemRawRecoveryScanner.ScanAsync(terminal.SessionDirectory!);
        Assert.True(recovery.IsComplete);
        Assert.Equal(4, recovery.VerifiedBlocks);
    }

    [Fact]
    public async Task Slow_required_processing_backpressures_without_loss_and_queue_is_bounded()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new ConcurrentQueue<long>();

        await using var router = new StreamRouter<int>();
        var processing = router.RegisterBranch(
            new StreamBranchOptions(
                "processing",
                "Required processing",
                Capacity: 2,
                Delivery: StreamBranchDelivery.Required,
                Overflow: StreamOverflowPolicy.Wait,
                FailurePolicy: StreamBranchFailurePolicy.Propagate),
            async (item, token) =>
            {
                delivered.Enqueue(item.PublishSequence);
                if (item.PublishSequence == 1)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(token);
                }
            });

        router.Start();

        await router.PublishAsync(1);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await router.PublishAsync(2);
        await router.PublishAsync(3);

        var blocked = router.PublishAsync(4).AsTask();
        await Task.Yield();

        Assert.False(blocked.IsCompleted);
        var saturated = processing.GetSnapshot();
        Assert.InRange(saturated.QueueDepth, 0, saturated.Capacity);
        Assert.InRange(saturated.HighWatermark, 0, saturated.Capacity);
        Assert.Equal(0, saturated.Dropped);
        Assert.Equal(0, saturated.Rejected);

        release.TrySetResult();
        var fourth = await blocked.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(fourth.HasRequiredFailure);

        var terminal = await router.CompleteAsync(StreamCompletionMode.Drain)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        var snapshot = processing.GetSnapshot();

        Assert.Equal(StreamRouterState.Completed, terminal.State);
        Assert.Equal(4, snapshot.Delivered);
        Assert.Equal(0, snapshot.Dropped);
        Assert.Equal(0, snapshot.Rejected);
        Assert.Equal(0, snapshot.QueueDepth);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, delivered.ToArray());
    }

    [Fact]
    public async Task Slow_optional_presentation_drops_without_blocking_required_processing()
    {
        var uiEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUi = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requiredDelivered = 0;

        await using var router = new StreamRouter<int>();
        var processing = router.RegisterBranch(
            new StreamBranchOptions(
                "processing",
                "Required processing",
                Capacity: 4,
                Delivery: StreamBranchDelivery.Required,
                Overflow: StreamOverflowPolicy.Wait,
                FailurePolicy: StreamBranchFailurePolicy.Propagate),
            (_, _) =>
            {
                Interlocked.Increment(ref requiredDelivered);
                return ValueTask.CompletedTask;
            });

        var presentation = router.RegisterBranch(
            new StreamBranchOptions(
                "presentation",
                "Optional presentation",
                Capacity: 1,
                Delivery: StreamBranchDelivery.Optional,
                Overflow: StreamOverflowPolicy.Latest,
                FailurePolicy: StreamBranchFailurePolicy.Isolate),
            async (item, token) =>
            {
                if (item.PublishSequence == 1)
                {
                    uiEntered.TrySetResult();
                    await releaseUi.Task.WaitAsync(token);
                }
            });

        router.Start();
        await router.PublishAsync(1);
        await uiEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        for (var value = 2; value <= 100; value++)
        {
            var result = await router.PublishAsync(value);
            Assert.False(result.HasRequiredFailure);
        }

        var liveUi = presentation.GetSnapshot();
        Assert.True(liveUi.Dropped > 0);
        Assert.InRange(liveUi.QueueDepth, 0, liveUi.Capacity);
        Assert.InRange(liveUi.HighWatermark, 0, liveUi.Capacity);

        releaseUi.TrySetResult();
        var terminal = await router.CompleteAsync(StreamCompletionMode.Drain)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StreamRouterState.Completed, terminal.State);
        Assert.Equal(100, Volatile.Read(ref requiredDelivered));
        Assert.Equal(100, processing.GetSnapshot().Delivered);
        Assert.Equal(0, processing.GetSnapshot().Dropped);
        Assert.Equal(0, processing.GetSnapshot().Rejected);
        Assert.Equal(0, processing.GetSnapshot().QueueDepth);
        Assert.Equal(0, presentation.GetSnapshot().QueueDepth);
    }

    [Fact]
    public async Task Pr_short_soak_preserves_raw_sequence_payload_and_quiesces()
    {
        await RunShortSoakAsync(seed: 6201, blockCount: 1_000)
            .WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static async Task RunShortSoakAsync(int seed, int blockCount)
    {
        using var temp = new TemporaryDirectory();
        await using var recorder = new FileSystemRawRecorder(Options(temp.Path, queueCapacity: 16));
        await recorder.PrepareAsync(Descriptor($"verification-soak-{seed}"));

        var processed = 0;
        await using var router = new StreamRouter<CanonicalRawBlock>();
        var processing = router.RegisterBranch(
            new StreamBranchOptions(
                "processing",
                "Required identity processing",
                Capacity: 16,
                Delivery: StreamBranchDelivery.Required,
                Overflow: StreamOverflowPolicy.Wait,
                FailurePolicy: StreamBranchFailurePolicy.Propagate),
            (_, _) =>
            {
                Interlocked.Increment(ref processed);
                return ValueTask.CompletedTask;
            });
        var presentation = router.RegisterBranch(
            new StreamBranchOptions(
                "presentation",
                "Optional preview",
                Capacity: 2,
                Delivery: StreamBranchDelivery.Optional,
                Overflow: StreamOverflowPolicy.DropOldest,
                FailurePolicy: StreamBranchFailurePolicy.Isolate),
            static (_, _) => ValueTask.CompletedTask);

        router.Start();

        for (var sequence = 1; sequence <= blockCount; sequence++)
        {
            var block = Block(sequence, seed);
            var accepted = await recorder.AcceptAsync(block);
            Assert.True(accepted.Accepted, accepted.Reason);

            var routed = await router.PublishAsync(block);
            Assert.False(routed.HasRequiredFailure);
            Assert.InRange(recorder.Snapshot.QueueDepth, 0, recorder.Snapshot.QueueCapacity);
            Assert.InRange(recorder.Snapshot.QueueHighWater, 0, recorder.Snapshot.QueueCapacity);
        }

        await recorder.StopAsync();
        await recorder.FinalizeAsync();
        var routerTerminal = await router.CompleteAsync(StreamCompletionMode.Drain);

        var raw = recorder.Snapshot;
        Assert.Equal(RawRecorderState.Completed, raw.State);
        Assert.Equal(blockCount, raw.AcceptedBlocks);
        Assert.Equal(blockCount, raw.WrittenBlocks);
        Assert.Equal(raw.AcceptedBytes, raw.WrittenBytes);
        Assert.Equal(0, raw.SequenceGapCount);
        Assert.Equal(0, raw.DuplicateBlockCount);
        Assert.Equal(0, raw.OutOfOrderBlockCount);
        Assert.Equal(0, raw.QueueDepth);
        Assert.InRange(raw.QueueHighWater, 0, raw.QueueCapacity);

        Assert.Equal(StreamRouterState.Completed, routerTerminal.State);
        Assert.Equal(blockCount, Volatile.Read(ref processed));
        Assert.Equal(blockCount, processing.GetSnapshot().Delivered);
        Assert.Equal(0, processing.GetSnapshot().QueueDepth);
        Assert.Equal(0, presentation.GetSnapshot().QueueDepth);

        var replay = await FileSystemRawArtifactReader.ReadAllAsync(raw.SessionDirectory!);
        Assert.Equal(blockCount, replay.Count);
        for (var index = 0; index < replay.Count; index++)
        {
            var expectedSequence = index + 1L;
            Assert.Equal(expectedSequence, replay[index].SequenceStart);
            Assert.Equal(Block(expectedSequence, seed).Payload.ToArray(), replay[index].Payload.ToArray());
        }

        var recovery = await FileSystemRawRecoveryScanner.ScanAsync(raw.SessionDirectory!);
        Assert.True(recovery.IsComplete);
        Assert.Empty(recovery.Issues);
        Assert.Equal(blockCount, recovery.VerifiedBlocks);
    }

    private static FileSystemRawRecorderOptions Options(string root, int queueCapacity) =>
        new(
            RootDirectory: root,
            QueueCapacity: queueCapacity,
            MaxSegmentBytes: 32 * 1024 * 1024,
            MaxSegmentDuration: TimeSpan.FromMinutes(2),
            HardMinimumFreeBytes: 0,
            WarningFreeBytes: 0,
            MaxSessionBytes: 256 * 1024 * 1024,
            Durability: RawDurabilityLevel.FlushOnFinalize,
            FreeSpaceCheckIntervalBlocks: 32);

    private static RawRecordingSessionDescriptor Descriptor(string sessionId) =>
        new(
            sessionId,
            [new RawRecordingSourceIdentity("synthetic-a", 1)],
            "verification-cfg-v1",
            DateTimeOffset.UnixEpoch);

    private static CanonicalRawBlock Block(long sequence, int seed = 6201)
    {
        var payload = new byte[64];
        var state = unchecked((uint)(seed ^ (int)sequence * 397));
        for (var index = 0; index < payload.Length; index++)
        {
            state = unchecked(state * 1664525u + 1013904223u);
            payload[index] = (byte)(state >> 24);
        }

        return CanonicalRawBlock.CopyFrom(
            sourceId: "synthetic-a",
            deviceId: "synthetic",
            connectionEpoch: 1,
            sequenceStart: sequence,
            sequenceCount: 1,
            sampleCount: 32,
            channelCount: 1,
            channelLayoutId: "channel-0",
            numericRepresentation: RawNumericRepresentation.Int16,
            byteOrder: RawByteOrder.LittleEndian,
            sampleRateHz: 1000,
            deviceTimestamp: sequence,
            hostMonotonicTimestamp: sequence,
            wallClockTimestamp: DateTimeOffset.UnixEpoch.AddMilliseconds(sequence),
            qualityFlags: 0,
            protocolVersion: "synthetic-v1",
            canonicalizationVersion: "raw-v1",
            configurationHash: "verification-cfg-v1",
            payload: payload);
    }

    private sealed class GatedSegmentStreamFactory : IRawSegmentStreamFactory
    {
        private int _openCount;
        public TaskCompletionSource WriteEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseWrites { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Stream OpenWrite(string path) =>
            Interlocked.Increment(ref _openCount) == 1
                ? OpenNormal(path)
                : new GatedWriteStream(path, WriteEntered, ReleaseWrites);
    }

    private static FileStream OpenNormal(string path) =>
        new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    private sealed class GatedWriteStream(
        string path,
        TaskCompletionSource entered,
        TaskCompletionSource release) : Stream
    {
        private readonly FileStream _inner = OpenNormal(path);

        public override bool CanRead => false;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            WaitForRelease();
            _inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            WaitForRelease();
            _inner.Write(buffer);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            await _inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteArrayAsync(buffer, offset, count, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }

        private void WaitForRelease()
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        }

        private async Task WriteArrayAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            await _inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "OpenDeviceStudio.AcquisitionVerification",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
