using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using OpenDeviceStudio.Abstractions.Storage;
using OpenDeviceStudio.Acquisition;
using OpenDeviceStudio.Hosting;
using OpenDeviceStudio.Starters;
using OpenDeviceStudio.Storage.FileSystem;

namespace OpenDeviceStudio.Tests;

public sealed class RawRecorderTestsClosure
{
    [Fact]
    public async Task Short_soak_keeps_queue_bounded_and_replays_every_accepted_raw_block()
    {
        using var temp = new TemporaryDirectory();
        const int blockCount = 512;
        const int payloadBytes = 64;
        const int queueCapacity = 16;

        await using var recorder = new FileSystemRawRecorder(
            new FileSystemRawRecorderOptions(
                RootDirectory: temp.Path,
                QueueCapacity: queueCapacity,
                MaxSegmentBytes: 2 * 1024,
                MaxSegmentDuration: TimeSpan.FromMinutes(1),
                HardMinimumFreeBytes: 0,
                WarningFreeBytes: 0,
                MaxSessionBytes: 4 * 1024 * 1024,
                Durability: RawDurabilityLevel.FlushOnFinalize,
                FreeSpaceCheckIntervalBlocks: 32));

        await recorder.PrepareAsync(new RawRecordingSessionDescriptor(
            "raw-short-soak",
            [new RawRecordingSourceIdentity("source-soak", 1)],
            "cfg-short-soak",
            DateTimeOffset.UtcNow));

        for (var sequence = 0; sequence < blockCount; sequence++)
        {
            var payload = new byte[payloadBytes];
            for (var sample = 0; sample < payloadBytes / sizeof(short); sample++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(
                    payload.AsSpan(sample * sizeof(short), sizeof(short)),
                    checked((short)(sequence + sample)));
            }

            var accepted = await recorder.AcceptAsync(CreateBlock(
                sourceId: "source-soak",
                sequence: sequence,
                configurationHash: "cfg-short-soak",
                payload: payload));

            Assert.True(accepted.Accepted, accepted.Reason);
        }

        await recorder.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        await recorder.FinalizeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));

        var snapshot = recorder.Snapshot;
        Assert.Equal(RawRecorderState.Completed, snapshot.State);
        Assert.Equal(blockCount, snapshot.AcceptedBlocks);
        Assert.Equal(blockCount, snapshot.WrittenBlocks);
        Assert.Equal(snapshot.AcceptedBytes, snapshot.WrittenBytes);
        Assert.InRange(snapshot.QueueHighWater, 1, queueCapacity);
        Assert.InRange(snapshot.QueueDepth, 0, queueCapacity);
        Assert.True(snapshot.SegmentCount > 1);
        Assert.NotNull(snapshot.SessionDirectory);

        var replay = await FileSystemRawArtifactReader.ReadAllAsync(snapshot.SessionDirectory!);
        Assert.Equal(blockCount, replay.Count);
        Assert.Equal(0, replay[0].SequenceStart);
        Assert.Equal(blockCount - 1, replay[^1].SequenceStart);

        foreach (var index in new[] { 0, 127, 255, 383, blockCount - 1 })
        {
            var stored = replay[index];
            Assert.Equal(index, stored.SequenceStart);
            Assert.Equal("source-soak", stored.SourceId);
            Assert.Equal("cfg-short-soak", stored.ConfigurationHash);

            var expectedFirstSample = new byte[sizeof(short)];
            BinaryPrimitives.WriteInt16LittleEndian(expectedFirstSample, checked((short)index));
            Assert.Equal(expectedFirstSample, stored.Payload.Span[..sizeof(short)].ToArray());
        }

        var recovery = await FileSystemRawRecoveryScanner.ScanAsync(snapshot.SessionDirectory!);
        Assert.True(recovery.IsComplete);
        Assert.Empty(recovery.Issues);
        Assert.Equal(blockCount, recovery.VerifiedBlocks);
        Assert.Equal(0, recovery.SequenceGapCount);
        Assert.Equal(0, recovery.DuplicateBlockCount);
        Assert.Equal(0, recovery.OutOfOrderBlockCount);
    }

    [Fact]
    public async Task Runtime_low_disk_threshold_faults_recorder_without_silent_raw_drop()
    {
        using var temp = new TemporaryDirectory();
        var spaceProbe = new SequenceStorageSpaceProbe(long.MaxValue, 0);

        await using var recorder = new FileSystemRawRecorder(
            new FileSystemRawRecorderOptions(
                RootDirectory: temp.Path,
                QueueCapacity: 4,
                MaxSegmentBytes: 1024 * 1024,
                MaxSegmentDuration: TimeSpan.FromMinutes(1),
                HardMinimumFreeBytes: 1,
                WarningFreeBytes: 1,
                MaxSessionBytes: 1024 * 1024,
                Durability: RawDurabilityLevel.FlushOnFinalize,
                FreeSpaceCheckIntervalBlocks: 1),
            TimeProvider.System,
            FileSystemRawSegmentStreamFactory.Instance,
            spaceProbe);

        await recorder.PrepareAsync(new RawRecordingSessionDescriptor(
            "runtime-low-disk",
            [new RawRecordingSourceIdentity("source-low-disk", 1)],
            "cfg-low-disk",
            DateTimeOffset.UtcNow));

        var accepted = await recorder.AcceptAsync(CreateBlock(
            sourceId: "source-low-disk",
            sequence: 1,
            configurationHash: "cfg-low-disk",
            payload: [0x01, 0x00]));

        Assert.True(accepted.Accepted, accepted.Reason);
        await WaitUntilAsync(
            () => recorder.Snapshot.State == RawRecorderState.Faulted,
            TimeSpan.FromSeconds(5));

        Assert.Equal(RawRecorderState.Faulted, recorder.Snapshot.State);
        Assert.Contains(
            "below hard threshold",
            recorder.Snapshot.FaultReason ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

        var rejected = recorder.TryAccept(CreateBlock(
            sourceId: "source-low-disk",
            sequence: 2,
            configurationHash: "cfg-low-disk",
            payload: [0x02, 0x00]));
        Assert.Equal(RawRecorderAcceptStatus.Faulted, rejected.Status);

        await Assert.ThrowsAsync<IOException>(() => recorder.StopAsync().AsTask());
    }

    [Fact]
    public async Task Finalize_flush_failure_faults_recorder_and_never_marks_session_completed()
    {
        using var temp = new TemporaryDirectory();
        var streamFactory = new FailSegmentFlushStreamFactory();

        await using var recorder = new FileSystemRawRecorder(
            new FileSystemRawRecorderOptions(
                RootDirectory: temp.Path,
                QueueCapacity: 4,
                MaxSegmentBytes: 1024 * 1024,
                MaxSegmentDuration: TimeSpan.FromMinutes(1),
                HardMinimumFreeBytes: 0,
                WarningFreeBytes: 0,
                MaxSessionBytes: 1024 * 1024,
                Durability: RawDurabilityLevel.FlushOnFinalize,
                FreeSpaceCheckIntervalBlocks: 1),
            TimeProvider.System,
            streamFactory);

        await recorder.PrepareAsync(new RawRecordingSessionDescriptor(
            "flush-failure",
            [new RawRecordingSourceIdentity("source-flush", 1)],
            "cfg-flush",
            DateTimeOffset.UtcNow));

        var accepted = await recorder.AcceptAsync(CreateBlock(
            sourceId: "source-flush",
            sequence: 1,
            configurationHash: "cfg-flush",
            payload: [0x01, 0x00, 0x02, 0x00]));
        Assert.True(accepted.Accepted, accepted.Reason);
        await WaitUntilAsync(
            () => recorder.Snapshot.WrittenBlocks == 1,
            TimeSpan.FromSeconds(5));
        streamFactory.EnableFlushFailure();

        await Assert.ThrowsAnyAsync<Exception>(() => recorder.StopAsync().AsTask());

        var snapshot = recorder.Snapshot;
        Assert.Equal(RawRecorderState.Faulted, snapshot.State);
        Assert.Equal(1, snapshot.AcceptedBlocks);
        Assert.Equal(1, snapshot.WrittenBlocks);
        Assert.Equal(0, snapshot.FlushedBlocks);

        var rejected = recorder.TryAccept(CreateBlock(
            sourceId: "source-flush",
            sequence: 2,
            configurationHash: "cfg-flush",
            payload: [0x03, 0x00]));
        Assert.Equal(RawRecorderAcceptStatus.Faulted, rejected.Status);

        var sessionDirectory = Assert.IsType<string>(snapshot.SessionDirectory);
        var recovery = await FileSystemRawRecoveryScanner.ScanAsync(sessionDirectory);
        Assert.False(recovery.IsComplete);
        Assert.Contains(
            recovery.Issues,
            static issue => issue.Kind == RawRecoveryIssueKind.IncompleteSession);
    }

    [Fact]
    public async Task Source_scaffold_default_composition_emits_and_verifies_real_raw_artifact()
    {
        using var temp = new TemporaryDirectory();
        var builder = OpenDeviceStudioApplication.CreateBuilder();
        builder.Configuration["OpenDeviceStudio:Transport:Type"] = "Simulator";

        var section = OpenDeviceStudioRawRecordingOptions.SectionName;
        builder.Configuration[$"{section}:Enabled"] = "true";
        builder.Configuration[$"{section}:RootDirectory"] = temp.Path;
        builder.Configuration[$"{section}:QueueCapacity"] = "8";
        builder.Configuration[$"{section}:MaxSegmentBytes"] = "1048576";
        builder.Configuration[$"{section}:MaxSegmentMinutes"] = "1";
        builder.Configuration[$"{section}:HardMinimumFreeBytes"] = "0";
        builder.Configuration[$"{section}:WarningFreeBytes"] = "0";
        builder.Configuration[$"{section}:MaxSessionBytes"] = "10485760";
        builder.Configuration[$"{section}:FreeSpaceCheckIntervalBlocks"] = "1";

        builder.AddOpenDeviceStudioApplication();

        await using var app = builder.Build();
        await app.StartAsync();

        var factory = app.Services.GetRequiredService<IRawRecorderFactory>();
        Assert.True(factory.IsEnabled);
        Assert.Null(factory.DisabledReason);

        var manager = app.Services.GetRequiredService<AcquisitionSessionManager>();
        var processing = new CountingProcessingSink();
        RecordingSource? source = null;

        var expected = CreateBlock(
            sourceId: "source-scaffold",
            sequence: 42,
            configurationHash: "cfg-source-scaffold",
            payload: [0x34, 0x12, 0x78, 0x56]);

        source = new RecordingSource(
            "source-scaffold",
            async cancellationToken =>
            {
                Assert.NotNull(source!.Context);
                var ingress = source.Context!.CreateRawFirstIngress(processing);
                await ingress.PublishAsync(expected, cancellationToken);
            });

        await using var session = manager.CreateSession(new AcquisitionSessionDefinition(
            AcquisitionSessionMode.LiveAcquisition,
            [source],
            configuration: new AcquisitionSessionConfiguration(
                new Dictionary<string, string>
                {
                    ["raw.test.profile"] = "source-scaffold-artifact-e2e"
                }),
            sessionId: "source-scaffold-raw-e2e"));

        await session.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var result = await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(AcquisitionSessionState.Completed, result.TerminalState);
        Assert.Equal(1, processing.Count);

        var sessionDirectory = Assert.Single(
            Directory.GetDirectories(temp.Path, "session-*", SearchOption.TopDirectoryOnly));
        var replay = await FileSystemRawArtifactReader.ReadAllAsync(sessionDirectory);
        var stored = Assert.Single(replay);

        Assert.Equal(expected.SourceId, stored.SourceId);
        Assert.Equal(expected.ConnectionEpoch, stored.ConnectionEpoch);
        Assert.Equal(expected.SequenceStart, stored.SequenceStart);
        Assert.Equal(expected.SequenceCount, stored.SequenceCount);
        Assert.Equal(expected.SampleCount, stored.SampleCount);
        Assert.Equal(expected.ChannelLayoutId, stored.ChannelLayoutId);
        Assert.Equal(expected.NumericRepresentation, stored.NumericRepresentation);
        Assert.Equal(expected.ByteOrder, stored.ByteOrder);
        Assert.Equal(expected.ConfigurationHash, stored.ConfigurationHash);
        Assert.Equal(expected.Payload.ToArray(), stored.Payload.ToArray());

        var recovery = await FileSystemRawRecoveryScanner.ScanAsync(sessionDirectory);
        Assert.True(recovery.IsComplete);
        Assert.Empty(recovery.Issues);
        Assert.Equal(1, recovery.VerifiedBlocks);
    }

    private static CanonicalRawBlock CreateBlock(
        string sourceId,
        long sequence,
        string configurationHash,
        ReadOnlySpan<byte> payload) =>
        CanonicalRawBlock.CopyFrom(
            sourceId: sourceId,
            deviceId: $"device-{sourceId}",
            connectionEpoch: 1,
            sequenceStart: sequence,
            sequenceCount: 1,
            sampleCount: Math.Max(1, payload.Length / sizeof(short)),
            channelCount: 1,
            channelLayoutId: "channel-0",
            numericRepresentation: RawNumericRepresentation.Int16,
            byteOrder: RawByteOrder.LittleEndian,
            sampleRateHz: 1000,
            deviceTimestamp: sequence * 10,
            hostMonotonicTimestamp: sequence * 100,
            wallClockTimestamp: DateTimeOffset.UnixEpoch.AddMilliseconds(sequence),
            qualityFlags: 0,
            protocolVersion: "sim-v1",
            canonicalizationVersion: "raw-v1",
            configurationHash: configurationHash,
            payload: payload);

    private sealed class CountingProcessingSink : IAcquisitionProcessingSink<CanonicalRawBlock>
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);

        public ValueTask HandoffAsync(
            CanonicalRawBlock block,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _count);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingSource(
        string sourceId,
        Func<CancellationToken, ValueTask> startHook) : IAcquisitionSource
    {
        public string ComponentId { get; } = $"source:{sourceId}";
        public string SourceId { get; } = sourceId;
        public long ConnectionEpoch => 1;
        public bool IsReplay => false;
        public bool IsReadOnly => false;
        public RawSourceFlowControl RawFlowControl => RawSourceFlowControl.SupportsBackpressure;
        public AcquisitionComponentKind Kind => AcquisitionComponentKind.Source;
        public AcquisitionComponentContext? Context { get; private set; }

        public ValueTask PrepareAsync(
            AcquisitionComponentContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Context = context;
            return ValueTask.CompletedTask;
        }

        public ValueTask StartAsync(CancellationToken cancellationToken = default) =>
            startHook(cancellationToken);

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask FinalizeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask AbortAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Expected Raw Recorder state was not reached.");
            await Task.Yield();
        }
    }

    private sealed class SequenceStorageSpaceProbe : IRawStorageSpaceProbe
    {
        private readonly long[] _values;
        private int _index = -1;

        public SequenceStorageSpaceProbe(params long[] values)
        {
            if (values.Length == 0)
                throw new ArgumentException("At least one storage-space value is required.", nameof(values));
            _values = values;
        }

        public long? GetAvailableFreeBytes(string path)
        {
            _ = path;
            var index = Interlocked.Increment(ref _index);
            return _values[Math.Min(index, _values.Length - 1)];
        }
    }

    private sealed class FailSegmentFlushStreamFactory : IRawSegmentStreamFactory
    {
        private int _openCount;
        private int _failFlush;

        public void EnableFlushFailure() => Volatile.Write(ref _failFlush, 1);

        public Stream OpenWrite(string path)
        {
            var stream = FileSystemRawSegmentStreamFactory.Instance.OpenWrite(path);
            return Interlocked.Increment(ref _openCount) == 1
                ? stream
                : new FlushFailingStream(
                    stream,
                    () => Volatile.Read(ref _failFlush) != 0);
        }
    }

    private sealed class FlushFailingStream(
        Stream inner,
        Func<bool> shouldFailFlush) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush()
        {
            if (shouldFailFlush())
                throw new IOException("Injected Raw segment flush failure.");
            inner.Flush();
        }

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            shouldFailFlush()
                ? Task.FromException(new IOException("Injected Raw segment flush failure."))
                : inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) =>
            inner.Seek(offset, origin);

        public override void SetLength(long value) =>
            inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) =>
            inner.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) =>
            inner.Write(buffer);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            inner.WriteAsync(buffer, offset, count, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "opendevicestudio-raw-closure-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
