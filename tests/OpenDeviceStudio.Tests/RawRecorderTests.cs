using Microsoft.Extensions.DependencyInjection;
using OpenDeviceStudio.Abstractions.Storage;
using OpenDeviceStudio.Acquisition;
using OpenDeviceStudio.Hosting;
using OpenDeviceStudio.Starters;
using OpenDeviceStudio.Storage.FileSystem;

namespace OpenDeviceStudio.Tests;

public sealed class RawRecorderTests
{
    [Fact]
    public void Canonical_raw_block_owns_an_exact_copy_of_source_bytes()
    {
        var payload = new byte[] { 0x01, 0x80, 0x34, 0x12, 0xff, 0x7f, 0x00, 0x80 };
        var expected = payload.ToArray();

        var block = Block(10, payload);

        payload[0] ^= 0xff;

        Assert.Equal(expected, block.Payload.ToArray());
        Assert.Equal(RawNumericRepresentation.Int16, block.NumericRepresentation);
        Assert.Equal(RawByteOrder.LittleEndian, block.ByteOrder);
        Assert.Equal(10, block.SequenceStart);
        Assert.Equal(11, block.SequenceEndExclusive);
    }

    [Fact]
    public async Task Session_default_raw_recorder_is_ready_before_source_and_raw_acceptance_precedes_processing()
    {
        using var temp = new TemporaryDirectory();
        var factory = new CapturingRawRecorderFactory(Options(temp.Path, queueCapacity: 4));
        var services = new ServiceCollection();
        services.AddSingleton<IRawRecorderFactory>(factory);
        services.AddOpenDeviceStudioAcquisition();

        await using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<AcquisitionSessionManager>();
        var block = Block(1, [0x01, 0x00, 0x02, 0x00]);
        var processing = new RecorderAwareProcessingSink(factory);
        RecordingSource? source = null;

        source = new RecordingSource(
            "source-a",
            async cancellationToken =>
            {
                Assert.NotNull(source!.Context);
                Assert.NotNull(factory.Recorder);
                Assert.Equal(RawRecorderState.Ready, factory.Recorder!.Snapshot.State);

                var ingress = source.Context!.CreateRawFirstIngress(processing);
                await ingress.PublishAsync(block, cancellationToken);
            });

        await using var session = manager.CreateSession(new AcquisitionSessionDefinition(
            AcquisitionSessionMode.LiveAcquisition,
            [source],
            sessionId: "raw-first-integration"));

        await session.StartAsync();
        var result = await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(AcquisitionSessionState.Completed, result.TerminalState);
        Assert.True(processing.SawRawAcceptedBeforeProcessing);
        Assert.Equal(1, processing.Count);

        var recorder = Assert.IsType<FileSystemRawRecorder>(factory.Recorder);
        var snapshot = recorder.Snapshot;
        Assert.Equal(RawRecorderState.Disposed, snapshot.State);
        Assert.Equal(1, snapshot.AcceptedBlocks);
        Assert.Equal(1, snapshot.WrittenBlocks);
        Assert.Equal(snapshot.AcceptedBytes, snapshot.WrittenBytes);
        Assert.NotNull(snapshot.SessionDirectory);

        var replay = await FileSystemRawArtifactReader.ReadAllAsync(snapshot.SessionDirectory!);
        var stored = Assert.Single(replay);
        Assert.Equal(block.SourceId, stored.SourceId);
        Assert.Equal(block.ConnectionEpoch, stored.ConnectionEpoch);
        Assert.Equal(block.SequenceStart, stored.SequenceStart);
        Assert.Equal(block.Payload.ToArray(), stored.Payload.ToArray());

        var recovery = await FileSystemRawRecoveryScanner.ScanAsync(snapshot.SessionDirectory!);
        Assert.True(recovery.IsComplete);
        Assert.Empty(recovery.Issues);
        Assert.Equal(1, recovery.VerifiedBlocks);
    }

    [Fact]
    public async Task Accept_means_recorder_owned_queue_acceptance_not_physical_write()
    {
        using var temp = new TemporaryDirectory();
        var streamFactory = new GatedSegmentStreamFactory();
        await using var recorder = new FileSystemRawRecorder(
            Options(temp.Path, queueCapacity: 2),
            TimeProvider.System,
            streamFactory);

        await recorder.PrepareAsync(Descriptor("accept-vs-write"));

        var accepted = await recorder.AcceptAsync(Block(1, [1, 2, 3, 4]))
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(accepted.Accepted);
        await streamFactory.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var snapshot = recorder.Snapshot;
        Assert.Equal(1, snapshot.AcceptedBlocks);
        Assert.Equal(0, snapshot.WrittenBlocks);
        Assert.InRange(snapshot.QueueDepth, 0, snapshot.QueueCapacity);

        streamFactory.ReleaseWrites.TrySetResult();
        await recorder.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await recorder.FinalizeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RawRecorderState.Completed, recorder.Snapshot.State);
        Assert.Equal(1, recorder.Snapshot.WrittenBlocks);
    }

    [Fact]
    public async Task Nonbackpressure_try_accept_overload_faults_instead_of_dropping_raw()
    {
        using var temp = new TemporaryDirectory();
        var streamFactory = new GatedSegmentStreamFactory();
        await using var recorder = new FileSystemRawRecorder(
            Options(temp.Path, queueCapacity: 1),
            TimeProvider.System,
            streamFactory);

        await recorder.PrepareAsync(Descriptor("overload"));

        Assert.True(recorder.TryAccept(Block(1, [1, 0])).Accepted);
        await streamFactory.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(recorder.TryAccept(Block(2, [2, 0])).Accepted);
        var overloaded = recorder.TryAccept(Block(3, [3, 0]));

        Assert.Equal(RawRecorderAcceptStatus.Overloaded, overloaded.Status);
        Assert.Equal(RawRecorderState.Faulted, recorder.Snapshot.State);
        Assert.Equal(2, recorder.Snapshot.AcceptedBlocks);

        streamFactory.ReleaseWrites.TrySetResult();

        await Assert.ThrowsAsync<IOException>(
            async () => await recorder.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Writer_io_failure_faults_recorder_and_future_ingress_is_rejected()
    {
        using var temp = new TemporaryDirectory();
        var streamFactory = new ThrowingSegmentStreamFactory();
        await using var recorder = new FileSystemRawRecorder(
            Options(temp.Path, queueCapacity: 2),
            TimeProvider.System,
            streamFactory);

        await recorder.PrepareAsync(Descriptor("writer-fault"));

        var first = recorder.TryAccept(Block(1, [1, 0]));
        Assert.True(first.Accepted);

        await streamFactory.WriteAttempted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(
            () => recorder.Snapshot.State == RawRecorderState.Faulted,
            TimeSpan.FromSeconds(2));

        var second = recorder.TryAccept(Block(2, [2, 0]));

        Assert.Equal(RawRecorderAcceptStatus.Faulted, second.Status);
        Assert.Contains("writer", recorder.Snapshot.FaultReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        await Assert.ThrowsAsync<IOException>(
            async () => await recorder.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Recovery_reports_incomplete_session_without_mutating_evidence()
    {
        using var temp = new TemporaryDirectory();
        await using var recorder = new FileSystemRawRecorder(Options(temp.Path));

        await recorder.PrepareAsync(Descriptor("incomplete"));
        var directory = Assert.IsType<string>(recorder.Snapshot.SessionDirectory);

        var before = Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .Select(static path => (Path: path, Length: new FileInfo(path).Length))
            .ToArray();

        var report = await FileSystemRawRecoveryScanner.ScanAsync(directory);

        Assert.False(report.IsComplete);
        Assert.Equal("Ready", report.ManifestState);
        Assert.Contains(
            report.Issues,
            static issue => issue.Kind == RawRecoveryIssueKind.IncompleteSession);

        var after = Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .Select(static path => (Path: path, Length: new FileInfo(path).Length))
            .ToArray();
        Assert.Equal(before, after);

        await recorder.AbortAsync("test cleanup");
    }

    [Fact]
    public async Task Recovery_detects_completed_artifact_corruption()
    {
        using var temp = new TemporaryDirectory();
        await using var recorder = new FileSystemRawRecorder(Options(temp.Path));

        await recorder.PrepareAsync(Descriptor("corruption"));
        Assert.True((await recorder.AcceptAsync(Block(1, [1, 0, 2, 0]))).Accepted);
        Assert.True((await recorder.AcceptAsync(Block(2, [3, 0, 4, 0]))).Accepted);
        await recorder.StopAsync();
        await recorder.FinalizeAsync();

        var directory = Assert.IsType<string>(recorder.Snapshot.SessionDirectory);
        var segment = Assert.Single(Directory.GetFiles(directory, "*.arrow", SearchOption.TopDirectoryOnly));

        await using (var stream = new FileStream(
                         segment,
                         FileMode.Open,
                         FileAccess.ReadWrite,
                         FileShare.Read))
        {
            Assert.True(stream.Length > 32);
            var offset = Math.Min(stream.Length - 1, Math.Max(16, stream.Length / 2));
            stream.Position = offset;
            var current = stream.ReadByte();
            Assert.NotEqual(-1, current);
            stream.Position = offset;
            stream.WriteByte((byte)(current ^ 0x5a));
            await stream.FlushAsync();
        }

        var report = await FileSystemRawRecoveryScanner.ScanAsync(directory);

        Assert.False(report.IsComplete);
        Assert.Contains(
            report.Issues,
            static issue =>
                issue.Kind is RawRecoveryIssueKind.ChecksumMismatch
                    or RawRecoveryIssueKind.CorruptSegment
                    or RawRecoveryIssueKind.TruncatedTail);
    }


    [Fact]
    public async Task Recovery_preserves_complete_prefix_before_truncated_tail()
    {
        using var temp = new TemporaryDirectory();
        await using var recorder = new FileSystemRawRecorder(Options(temp.Path));

        await recorder.PrepareAsync(Descriptor("truncated-prefix"));
        for (var sequence = 1; sequence <= 4; sequence++)
        {
            var payload = Enumerable.Repeat((byte)sequence, 4096).ToArray();
            Assert.True((await recorder.AcceptAsync(Block(sequence, payload))).Accepted);
        }

        await recorder.StopAsync();
        await recorder.FinalizeAsync();

        var directory = Assert.IsType<string>(recorder.Snapshot.SessionDirectory);
        var segment = Assert.Single(Directory.GetFiles(directory, "*.arrow", SearchOption.TopDirectoryOnly));

        await using (var stream = new FileStream(
                         segment,
                         FileMode.Open,
                         FileAccess.ReadWrite,
                         FileShare.Read))
        {
            Assert.True(stream.Length > 4096);
            var remove = Math.Min(2048, Math.Max(256, stream.Length / 8));
            stream.SetLength(stream.Length - remove);
            await stream.FlushAsync();
        }

        var report = await FileSystemRawRecoveryScanner.ScanAsync(directory);

        Assert.InRange(report.VerifiedBlocks, 1, 3);
        Assert.Contains(
            report.Issues,
            static issue =>
                issue.Kind is RawRecoveryIssueKind.CorruptSegment
                    or RawRecoveryIssueKind.TruncatedTail
                    or RawRecoveryIssueKind.ChecksumMismatch);
    }

    [Fact]
    public async Task CannotBackpressure_source_uses_try_raw_accept_path()
    {
        var raw = new DualRawSink();
        var processing = new CountingIntProcessingSink();
        RecordingSource? source = null;

        source = new RecordingSource(
            "source-callback",
            async cancellationToken =>
            {
                var ingress = source!.Context!.CreateRawFirstIngress<int>(raw, processing);
                await ingress.PublishAsync(42, cancellationToken);
            },
            RawSourceFlowControl.CannotBackpressure);

        await using var manager = new AcquisitionSessionManager();
        await using var session = manager.CreateSession(new AcquisitionSessionDefinition(
            AcquisitionSessionMode.LiveAcquisition,
            [source]));

        await session.StartAsync();
        var result = await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(AcquisitionSessionState.Completed, result.TerminalState);
        Assert.Equal(0, raw.AsyncAcceptCount);
        Assert.Equal(1, raw.TryAcceptCount);
        Assert.Equal(1, processing.Count);
    }

    [Fact]
    public async Task Preflight_open_failure_prevents_recorder_ready()
    {
        using var temp = new TemporaryDirectory();
        await using var recorder = new FileSystemRawRecorder(
            Options(temp.Path),
            TimeProvider.System,
            new OpenFailingSegmentStreamFactory());

        await Assert.ThrowsAsync<IOException>(
            async () => await recorder.PrepareAsync(Descriptor("preflight-failure")).AsTask());

        Assert.NotEqual(RawRecorderState.Ready, recorder.Snapshot.State);
        Assert.NotEqual(RawRecorderState.Running, recorder.Snapshot.State);
    }

    [Fact]
    public async Task Concurrent_accepts_cannot_oversubscribe_session_quota()
    {
        using var temp = new TemporaryDirectory();
        var streamFactory = new GatedSegmentStreamFactory();
        await using var recorder = new FileSystemRawRecorder(
            Options(temp.Path, queueCapacity: 4, maxSessionBytes: 4),
            TimeProvider.System,
            streamFactory);

        await recorder.PrepareAsync(Descriptor("quota-atomic"));

        var firstTask = Task.Run(() => recorder.TryAccept(Block(1, [1, 0, 2, 0])));
        var secondTask = Task.Run(() => recorder.TryAccept(Block(2, [3, 0, 4, 0])));
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.Single(results, static result => result.Status == RawRecorderAcceptStatus.Accepted);
        Assert.Single(results, static result => result.Status == RawRecorderAcceptStatus.Faulted);
        Assert.Equal(4, recorder.Snapshot.AcceptedBytes);
        Assert.Equal(1, recorder.Snapshot.AcceptedBlocks);
        Assert.Equal(RawRecorderState.Faulted, recorder.Snapshot.State);

        streamFactory.ReleaseWrites.TrySetResult();
        await Assert.ThrowsAsync<IOException>(
            async () => await recorder.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Faulted_dispose_waits_for_inflight_writer_before_releasing_segment()
    {
        using var temp = new TemporaryDirectory();
        var streamFactory = new GatedSegmentStreamFactory();
        var recorder = new FileSystemRawRecorder(
            Options(temp.Path, queueCapacity: 2, maxSessionBytes: 2),
            TimeProvider.System,
            streamFactory);

        await recorder.PrepareAsync(Descriptor("faulted-dispose"));
        Assert.True(recorder.TryAccept(Block(1, [1, 0])).Accepted);
        await streamFactory.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var quotaFault = recorder.TryAccept(Block(2, [2, 0]));
        Assert.Equal(RawRecorderAcceptStatus.Faulted, quotaFault.Status);
        Assert.Equal(RawRecorderState.Faulted, recorder.Snapshot.State);

        var disposing = recorder.DisposeAsync().AsTask();
        await Task.Yield();
        Assert.False(disposing.IsCompleted);

        streamFactory.ReleaseWrites.TrySetResult();
        await disposing.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RawRecorderState.Disposed, recorder.Snapshot.State);
        await recorder.DisposeAsync();
    }

    [Fact]
    public async Task Sequence_gap_duplicate_and_out_of_order_are_recorded_and_recoverable()
    {
        using var temp = new TemporaryDirectory();
        await using var recorder = new FileSystemRawRecorder(Options(temp.Path));

        await recorder.PrepareAsync(Descriptor("sequence-diagnostics"));
        Assert.True((await recorder.AcceptAsync(BlockRange(10, 1, [10, 0]))).Accepted);
        Assert.True((await recorder.AcceptAsync(BlockRange(12, 1, [12, 0]))).Accepted);
        Assert.True((await recorder.AcceptAsync(BlockRange(12, 1, [12, 0]))).Accepted);
        Assert.True((await recorder.AcceptAsync(BlockRange(12, 2, [12, 0, 13, 0]))).Accepted);
        await recorder.StopAsync();
        await recorder.FinalizeAsync();

        var snapshot = recorder.Snapshot;
        Assert.Equal(1, snapshot.SequenceGapCount);
        Assert.Equal(1, snapshot.DuplicateBlockCount);
        Assert.Equal(1, snapshot.OutOfOrderBlockCount);
        Assert.Equal(13, Assert.Single(snapshot.LastSequences).LastSequence);

        var directory = Assert.IsType<string>(snapshot.SessionDirectory);
        var recovery = await FileSystemRawRecoveryScanner.ScanAsync(directory);

        Assert.Equal(1, recovery.SequenceGapCount);
        Assert.Equal(1, recovery.DuplicateBlockCount);
        Assert.Equal(1, recovery.OutOfOrderBlockCount);
    }

    [Fact]
    public async Task Flush_on_finalize_distinguishes_written_from_flushed_and_durable()
    {
        using var temp = new TemporaryDirectory();
        await using var recorder = new FileSystemRawRecorder(
            Options(temp.Path, durability: RawDurabilityLevel.FlushOnFinalize));

        await recorder.PrepareAsync(Descriptor("flush-semantics"));
        Assert.True((await recorder.AcceptAsync(Block(1, [1, 0]))).Accepted);
        Assert.True((await recorder.AcceptAsync(Block(2, [2, 0]))).Accepted);

        await recorder.StopAsync();
        await recorder.FinalizeAsync();

        var snapshot = recorder.Snapshot;
        Assert.Equal(2, snapshot.WrittenBlocks);
        Assert.Equal(2, snapshot.FlushedBlocks);
        Assert.Equal(0, snapshot.DurableBlocks);
        Assert.Equal(snapshot.WrittenBytes, snapshot.FlushedBytes);
        Assert.Equal(0, snapshot.DurableBytes);
    }

    [Fact]
    public async Task Recovery_reports_uncommitted_temp_manifest_without_mutating_it()
    {
        using var temp = new TemporaryDirectory();
        await using var recorder = new FileSystemRawRecorder(Options(temp.Path));

        await recorder.PrepareAsync(Descriptor("manifest-temp"));
        var directory = Assert.IsType<string>(recorder.Snapshot.SessionDirectory);
        var tempManifest = System.IO.Path.Combine(directory, "manifest.json.tmp");
        await File.WriteAllTextAsync(tempManifest, "{\"generation\":999}");

        var before = await File.ReadAllBytesAsync(tempManifest);
        var report = await FileSystemRawRecoveryScanner.ScanAsync(directory);
        var after = await File.ReadAllBytesAsync(tempManifest);

        Assert.Contains(
            report.Issues,
            static issue => issue.Kind == RawRecoveryIssueKind.UncommittedManifest);
        Assert.Equal(before, after);

        File.Delete(tempManifest);
        await recorder.AbortAsync("test cleanup");
    }

    [Fact]
    public async Task Starter_raw_recording_is_default_on_and_opt_out_requires_audited_reason()
    {
        var defaults = OpenDeviceStudioApplication.CreateBuilder().AddOpenDeviceStudioDefaults();
        await using (var provider = defaults.Services.BuildServiceProvider())
        {
            var factory = provider.GetRequiredService<IRawRecorderFactory>();
            Assert.True(factory.IsEnabled);
            Assert.Null(factory.DisabledReason);
        }

        Assert.Throws<ArgumentException>(() =>
            OpenDeviceStudioApplication.CreateBuilder()
                .AddOpenDeviceStudioDefaults()
                .DisableRawRecording(" "));

        var configured = OpenDeviceStudioApplication.CreateBuilder();
        configured.Configuration[$"{OpenDeviceStudioRawRecordingOptions.SectionName}:Enabled"] = "false";
        configured.Configuration[$"{OpenDeviceStudioRawRecordingOptions.SectionName}:DisabledReason"] =
            "Product policy test opt-out";
        configured
            .AddOpenDeviceStudioDefaults()
            .AddConfiguredRawRecording();

        await using var disabledProvider = configured.Services.BuildServiceProvider();
        var disabled = disabledProvider.GetRequiredService<IRawRecorderFactory>();

        Assert.False(disabled.IsEnabled);
        Assert.Equal("Product policy test opt-out", disabled.DisabledReason);
    }

    private static FileSystemRawRecorderOptions Options(
        string root,
        int queueCapacity = 8,
        long maxSegmentBytes = 1024 * 1024,
        long maxSessionBytes = 16 * 1024 * 1024,
        RawDurabilityLevel durability = RawDurabilityLevel.Buffered) =>
        new(
            RootDirectory: root,
            QueueCapacity: queueCapacity,
            MaxSegmentBytes: maxSegmentBytes,
            MaxSegmentDuration: TimeSpan.FromMinutes(1),
            HardMinimumFreeBytes: 0,
            WarningFreeBytes: 0,
            MaxSessionBytes: maxSessionBytes,
            Durability: durability,
            FreeSpaceCheckIntervalBlocks: 1);

    private static RawRecordingSessionDescriptor Descriptor(string sessionId) =>
        new(
            sessionId,
            [new RawRecordingSourceIdentity("source-a", 1)],
            "cfg-001",
            DateTimeOffset.UtcNow);

    private static CanonicalRawBlock Block(long sequence, ReadOnlySpan<byte> payload) =>
        BlockRange(sequence, 1, payload);

    private static CanonicalRawBlock BlockRange(
        long sequence,
        int sequenceCount,
        ReadOnlySpan<byte> payload) =>
        CanonicalRawBlock.CopyFrom(
            sourceId: "source-a",
            deviceId: "device-a",
            connectionEpoch: 1,
            sequenceStart: sequence,
            sequenceCount: sequenceCount,
            sampleCount: Math.Max(1, payload.Length / 2),
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
            configurationHash: "cfg-001",
            payload: payload);

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Expected Raw Recorder condition was not reached.");
            await Task.Yield();
        }
    }

    private sealed class CapturingRawRecorderFactory(FileSystemRawRecorderOptions options)
        : IRawRecorderFactory
    {
        public FileSystemRawRecorder? Recorder { get; private set; }

        public bool IsEnabled => true;
        public string? DisabledReason => null;

        public IRawRecorder Create()
        {
            if (Recorder is not null)
                throw new InvalidOperationException("Test factory only supports one recorder.");
            Recorder = new FileSystemRawRecorder(options);
            return Recorder;
        }
    }

    private sealed class RecorderAwareProcessingSink(CapturingRawRecorderFactory factory)
        : IAcquisitionProcessingSink<CanonicalRawBlock>
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);
        public bool SawRawAcceptedBeforeProcessing { get; private set; }

        public ValueTask HandoffAsync(
            CanonicalRawBlock block,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SawRawAcceptedBeforeProcessing =
                factory.Recorder is not null &&
                factory.Recorder.Snapshot.AcceptedBlocks >= 1;
            Interlocked.Increment(ref _count);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingSource(
        string sourceId,
        Func<CancellationToken, ValueTask> startHook,
        RawSourceFlowControl rawFlowControl = RawSourceFlowControl.SupportsBackpressure) : IAcquisitionSource
    {
        public string ComponentId { get; } = $"source:{sourceId}";
        public string SourceId { get; } = sourceId;
        public long ConnectionEpoch => 1;
        public bool IsReplay => false;
        public bool IsReadOnly => false;
        public RawSourceFlowControl RawFlowControl => rawFlowControl;
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

    private sealed class DualRawSink :
        IAcquisitionRawSink<int>,
        IAcquisitionTryRawSink<int>
    {
        private int _asyncAcceptCount;
        private int _tryAcceptCount;

        public int AsyncAcceptCount => Volatile.Read(ref _asyncAcceptCount);
        public int TryAcceptCount => Volatile.Read(ref _tryAcceptCount);

        public ValueTask<AcquisitionRawAcceptance> AcceptAsync(
            int block,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _asyncAcceptCount);
            return ValueTask.FromResult(AcquisitionRawAcceptance.Success);
        }

        public AcquisitionRawAcceptance TryAccept(int block)
        {
            Interlocked.Increment(ref _tryAcceptCount);
            return AcquisitionRawAcceptance.Success;
        }
    }

    private sealed class CountingIntProcessingSink : IAcquisitionProcessingSink<int>
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);

        public ValueTask HandoffAsync(
            int block,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _count);
            return ValueTask.CompletedTask;
        }
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

    private sealed class ThrowingSegmentStreamFactory : IRawSegmentStreamFactory
    {
        private int _openCount;

        public TaskCompletionSource WriteAttempted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Stream OpenWrite(string path) =>
            Interlocked.Increment(ref _openCount) == 1
                ? OpenNormal(path)
                : new ThrowingWriteStream(path, WriteAttempted);
    }

    private sealed class OpenFailingSegmentStreamFactory : IRawSegmentStreamFactory
    {
        public Stream OpenWrite(string path) =>
            throw new IOException("Injected Raw recorder preflight open failure.");
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
        private readonly FileStream _inner = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        public override bool CanRead => false;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _inner.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) =>
            _inner.Seek(offset, origin);

        public override void SetLength(long value) => _inner.SetLength(value);

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

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

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            await _inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            WriteArrayAsync(buffer, offset, count, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
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

        private async Task WriteArrayAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            await _inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
        }
    }

    private sealed class ThrowingWriteStream(
        string path,
        TaskCompletionSource writeAttempted) : Stream
    {
        private readonly FileStream _inner = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        public override bool CanRead => false;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _inner.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) =>
            _inner.Seek(offset, origin);

        public override void SetLength(long value) => _inner.SetLength(value);

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            writeAttempted.TrySetResult();
            throw new IOException("Injected raw segment write failure.");
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            writeAttempted.TrySetResult();
            throw new IOException("Injected raw segment write failure.");
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            writeAttempted.TrySetResult();
            return ValueTask.FromException(new IOException("Injected raw segment write failure."));
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            writeAttempted.TrySetResult();
            return Task.FromException(new IOException("Injected raw segment write failure."));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "OpenDeviceStudio.Tests",
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

