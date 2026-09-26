using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using OpenDeviceStudio.Abstractions.Observability;
using OpenDeviceStudio.Abstractions.Storage;

namespace OpenDeviceStudio.Storage.FileSystem;

public sealed class FileSystemRawRecorder : IRawRecorder
{
    private readonly FileSystemRawRecorderOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IRawSegmentStreamFactory _streamFactory;
    private readonly IRawStorageSpaceProbe _storageSpaceProbe;
    private readonly object _gate = new();
    private readonly object _acceptGate = new();
    private readonly SemaphoreSlim _manifestGate = new(1, 1);
    private readonly CancellationTokenSource _faultCancellation = new();
    private readonly Dictionary<SegmentKey, SegmentWriter> _activeSegments = new();
    private readonly Dictionary<SegmentKey, long> _nextSequenceBySource = new();
    private readonly Dictionary<SegmentKey, long> _lastSequenceBySource = new();
    private readonly List<RawSegmentManifest> _segments = [];

    private RawRecordingSessionDescriptor? _descriptor;
    private IRawRecorderFaultObserver? _faultObserver;
    private Channel<CanonicalRawBlock>? _queue;
    private SemaphoreSlim? _slots;
    private Task _writerTask = Task.CompletedTask;
    private Task _faultManifestTask = Task.CompletedTask;
    private RawRecorderState _state = RawRecorderState.Created;
    private string? _sessionDirectory;
    private string? _manifestPath;
    private string? _faultReason;
    private long _acceptedBlocks;
    private long _writtenBlocks;
    private long _flushedBlocks;
    private long _durableBlocks;
    private long _acceptedBytes;
    private long _writtenBytes;
    private long _flushedBytes;
    private long _durableBytes;
    private long _sessionPayloadBytes;
    private long _sequenceGapCount;
    private long _duplicateBlockCount;
    private long _outOfOrderBlockCount;
    private long _manifestGeneration;
    private int _queueDepth;
    private int _queueHighWater;
    private int _segmentCount;
    private int _disposeOnce;
    private int _faultOnce;
    private long _writeOrdinal;

    public FileSystemRawRecorder(
        FileSystemRawRecorderOptions options,
        TimeProvider? timeProvider = null)
        : this(
            options,
            timeProvider ?? TimeProvider.System,
            FileSystemRawSegmentStreamFactory.Instance,
            DriveInfoRawStorageSpaceProbe.Instance)
    {
    }

    internal FileSystemRawRecorder(
        FileSystemRawRecorderOptions options,
        TimeProvider timeProvider,
        IRawSegmentStreamFactory streamFactory)
        : this(
            options,
            timeProvider,
            streamFactory,
            DriveInfoRawStorageSpaceProbe.Instance)
    {
    }

    internal FileSystemRawRecorder(
        FileSystemRawRecorderOptions options,
        TimeProvider timeProvider,
        IRawSegmentStreamFactory streamFactory,
        IRawStorageSpaceProbe storageSpaceProbe)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _streamFactory = streamFactory ?? throw new ArgumentNullException(nameof(streamFactory));
        _storageSpaceProbe = storageSpaceProbe ?? throw new ArgumentNullException(nameof(storageSpaceProbe));
    }

    public RawRecorderSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                var lastSequences = _lastSequenceBySource
                    .OrderBy(static pair => pair.Key.SourceId, StringComparer.Ordinal)
                    .ThenBy(static pair => pair.Key.ConnectionEpoch)
                    .Select(static pair => new RawSourceSequenceSnapshot(
                        pair.Key.SourceId,
                        pair.Key.ConnectionEpoch,
                        pair.Value))
                    .ToArray();

                return new RawRecorderSnapshot(
                    _state,
                    Interlocked.Read(ref _acceptedBlocks),
                    Interlocked.Read(ref _writtenBlocks),
                    Interlocked.Read(ref _flushedBlocks),
                    Interlocked.Read(ref _durableBlocks),
                    Interlocked.Read(ref _acceptedBytes),
                    Interlocked.Read(ref _writtenBytes),
                    Interlocked.Read(ref _flushedBytes),
                    Interlocked.Read(ref _durableBytes),
                    Math.Max(0, Volatile.Read(ref _queueDepth)),
                    _options.QueueCapacity,
                    Volatile.Read(ref _queueHighWater),
                    Volatile.Read(ref _segmentCount),
                    Interlocked.Read(ref _sequenceGapCount),
                    Interlocked.Read(ref _duplicateBlockCount),
                    Interlocked.Read(ref _outOfOrderBlockCount),
                    lastSequences,
                    _sessionDirectory,
                    _faultReason);
            }
        }
    }

    public async ValueTask PrepareAsync(
        RawRecordingSessionDescriptor descriptor,
        IRawRecorderFaultObserver? faultObserver = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        lock (_gate)
        {
            if (_state != RawRecorderState.Created)
                throw new InvalidOperationException($"Raw recorder cannot prepare from state {_state}.");
            _state = RawRecorderState.Preparing;
            _descriptor = descriptor;
            _faultObserver = faultObserver;
        }

        var root = Path.GetFullPath(_options.RootDirectory);
        Directory.CreateDirectory(root);
        EnsureDiskSpace(root);

        var safeSession = SafePathPart(descriptor.SessionId);
        var sessionDirectory = Path.GetFullPath(Path.Combine(root, $"session-{safeSession}"));
        if (!IsUnderRoot(root, sessionDirectory))
            throw new InvalidOperationException("Raw session path escaped the configured storage root.");

        if (Directory.Exists(sessionDirectory) &&
            Directory.EnumerateFileSystemEntries(sessionDirectory).Any())
        {
            throw new IOException(
                $"Raw session directory already contains data: {sessionDirectory}");
        }

        Directory.CreateDirectory(sessionDirectory);
        _sessionDirectory = sessionDirectory;
        _manifestPath = Path.Combine(sessionDirectory, "manifest.json");

        _slots = new SemaphoreSlim(_options.QueueCapacity, _options.QueueCapacity);
        _queue = Channel.CreateBounded<CanonicalRawBlock>(
            new BoundedChannelOptions(_options.QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

        await WriteManifestAsync("Preparing", null, cancellationToken).ConfigureAwait(false);
        await PreflightWriterAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate)
            _state = RawRecorderState.Ready;

        await WriteManifestAsync("Ready", null, cancellationToken).ConfigureAwait(false);
        _writerTask = WriterLoopAsync(_queue.Reader);
    }

    public async ValueTask<RawRecorderAcceptResult> AcceptAsync(
        CanonicalRawBlock block,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(block);

        var preflight = ValidateAccept(block);
        if (preflight is not null)
            return preflight;

        var slots = _slots ?? throw new InvalidOperationException("Raw recorder is not prepared.");
        var queue = _queue ?? throw new InvalidOperationException("Raw recorder is not prepared.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _faultCancellation.Token);

        try
        {
            await slots.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_faultCancellation.IsCancellationRequested)
        {
            return new RawRecorderAcceptResult(
                RawRecorderAcceptStatus.Faulted,
                _faultReason ?? "Raw recorder faulted.");
        }

        return EnqueueAcceptedBlock(block, queue, slots);
    }

    public RawRecorderAcceptResult TryAccept(CanonicalRawBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        var preflight = ValidateAccept(block);
        if (preflight is not null)
            return preflight;

        var slots = _slots ?? throw new InvalidOperationException("Raw recorder is not prepared.");
        var queue = _queue ?? throw new InvalidOperationException("Raw recorder is not prepared.");

        if (!slots.Wait(0))
        {
            var overload = new RawRecorderFault(
                "raw.queue.overload",
                "Canonical Raw recorder queue is full for a source that cannot backpressure.",
                null,
                _timeProvider.GetUtcNow());
            Fail(overload);
            return new RawRecorderAcceptResult(
                RawRecorderAcceptStatus.Overloaded,
                overload.Message);
        }

        return EnqueueAcceptedBlock(block, queue, slots);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        RawRecorderState state;
        string? faultReason;
        lock (_gate)
        {
            state = _state;
            faultReason = _faultReason;
            if (state is RawRecorderState.Completed or RawRecorderState.Aborted or RawRecorderState.Disposed)
                return;
            if (state is RawRecorderState.Created or RawRecorderState.Preparing)
                throw new InvalidOperationException($"Raw recorder cannot stop from state {state}.");
            if (state != RawRecorderState.Faulted)
                _state = RawRecorderState.Stopping;
        }

        if (state == RawRecorderState.Faulted)
        {
            Exception? writerFailure = null;
            try
            {
                await _writerTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                writerFailure = ex;
            }

            await _faultManifestTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            throw new IOException(faultReason ?? "Raw recorder faulted.", writerFailure);
        }

        _queue!.Writer.TryComplete();
        try
        {
            await _writerTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            await FinalizeActiveSegmentsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Fail(new RawRecorderFault(
                "raw.finalize.failure",
                "Raw recorder drain/segment finalization failed.",
                ex,
                _timeProvider.GetUtcNow()));
            await _faultManifestTask.ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask FinalizeAsync(CancellationToken cancellationToken = default)
    {
        if (Snapshot.State is RawRecorderState.Ready or RawRecorderState.Running)
            await StopAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            if (_state == RawRecorderState.Faulted)
                throw new IOException(_faultReason ?? "Raw recorder faulted.");
            if (_state == RawRecorderState.Completed)
                return;
            if (_state != RawRecorderState.Stopping)
                throw new InvalidOperationException($"Raw recorder cannot finalize from state {_state}.");
        }

        try
        {
            await WriteManifestAsync("Completed", null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Fail(new RawRecorderFault(
                "raw.manifest.finalize_failure",
                "Raw recorder final manifest commit failed.",
                ex,
                _timeProvider.GetUtcNow()));
            await _faultManifestTask.ConfigureAwait(false);
            throw;
        }

        lock (_gate)
            _state = RawRecorderState.Completed;
        RawRecorderTelemetry.SessionsCompleted.Add(1);
    }

    public async ValueTask AbortAsync(
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var state = Snapshot.State;
        if (state is RawRecorderState.Completed or RawRecorderState.Aborted or RawRecorderState.Disposed)
            return;

        _queue?.Writer.TryComplete();

        try
        {
            await _writerTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            await FinalizeActiveSegmentsAsync(cancellationToken).ConfigureAwait(false);
            await WriteManifestAsync("Aborted", reason, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await BestEffortManifestAsync("Incomplete", reason).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await BestEffortManifestAsync(
                "Incomplete",
                $"{reason}; abort finalization failed: {ex.Message}").ConfigureAwait(false);
        }

        lock (_gate)
        {
            if (_state != RawRecorderState.Faulted)
                _state = RawRecorderState.Aborted;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeOnce, 1) != 0)
            return;

        try
        {
            if (Snapshot.State is not (
                RawRecorderState.Completed
                or RawRecorderState.Aborted
                or RawRecorderState.Faulted))
            {
                await AbortAsync("Raw recorder disposed before normal completion.")
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                await _writerTask.ConfigureAwait(false);
            }
            catch
            {
                // Writer failure is already captured in recorder state/fault diagnostics.
            }

            foreach (var segment in _activeSegments.Values)
                await segment.DisposeAsync().ConfigureAwait(false);

            await _faultManifestTask.ConfigureAwait(false);
            _slots?.Dispose();
            _faultCancellation.Dispose();
            _manifestGate.Dispose();
            lock (_gate)
                _state = RawRecorderState.Disposed;
        }
    }

    private RawRecorderAcceptResult? ValidateAccept(CanonicalRawBlock block)
    {
        var state = Snapshot.State;
        if (state == RawRecorderState.Faulted)
            return new RawRecorderAcceptResult(
                RawRecorderAcceptStatus.Faulted,
                _faultReason ?? "Raw recorder faulted.");

        if (state is not (RawRecorderState.Ready or RawRecorderState.Running))
            return new RawRecorderAcceptResult(
                RawRecorderAcceptStatus.Closed,
                $"Raw recorder is not accepting blocks in state {state}.");

        var descriptor = _descriptor!;
        var source = descriptor.Sources.FirstOrDefault(item =>
            string.Equals(item.SourceId, block.SourceId, StringComparison.Ordinal));

        if (source is null || source.ConnectionEpoch != block.ConnectionEpoch)
        {
            var fault = new RawRecorderFault(
                "raw.source.identity_mismatch",
                $"Raw block source/epoch '{block.SourceId}/{block.ConnectionEpoch}' is not part of the frozen session.",
                null,
                _timeProvider.GetUtcNow());
            Fail(fault);
            return new RawRecorderAcceptResult(RawRecorderAcceptStatus.Faulted, fault.Message);
        }

        return null;
    }

    private RawRecorderAcceptResult EnqueueAcceptedBlock(
        CanonicalRawBlock block,
        Channel<CanonicalRawBlock> queue,
        SemaphoreSlim slots)
    {
        var ownsSlot = true;
        var reservedBytes = false;

        try
        {
            var recheck = ValidateAccept(block);
            if (recheck is not null)
                return recheck;

            if (!TryReserveSessionBytes(block.Payload.Length, out var quotaFailure))
                return quotaFailure;

            reservedBytes = true;
            var owned = block.CloneOwned();

            lock (_acceptGate)
            {
                if (!queue.Writer.TryWrite(owned))
                {
                    return new RawRecorderAcceptResult(
                        CurrentRejectStatus(),
                        _faultReason ?? "Raw recorder ingress is closed.");
                }

                ownsSlot = false;
                reservedBytes = false;
                TrackSequenceContinuity(owned);
                Accepted(owned);
                return RawRecorderAcceptResult.Success;
            }
        }
        finally
        {
            if (reservedBytes)
                ReleaseSessionBytes(block.Payload.Length);
            if (ownsSlot)
                slots.Release();
        }
    }

    private bool TryReserveSessionBytes(int payloadBytes, out RawRecorderAcceptResult failure)
    {
        while (true)
        {
            var current = Interlocked.Read(ref _acceptedBytes);
            if (payloadBytes > _options.MaxSessionBytes - current)
            {
                var fault = new RawRecorderFault(
                    "raw.session.quota",
                    "Raw recording session exceeded its configured byte quota.",
                    null,
                    _timeProvider.GetUtcNow());
                Fail(fault);
                failure = new RawRecorderAcceptResult(
                    RawRecorderAcceptStatus.Faulted,
                    fault.Message);
                return false;
            }

            if (Interlocked.CompareExchange(
                    ref _acceptedBytes,
                    current + payloadBytes,
                    current) == current)
            {
                failure = RawRecorderAcceptResult.Success;
                return true;
            }
        }
    }

    private void ReleaseSessionBytes(int payloadBytes) =>
        Interlocked.Add(ref _acceptedBytes, -payloadBytes);

    private void TrackSequenceContinuity(CanonicalRawBlock block)
    {
        var key = new SegmentKey(block.SourceId, block.ConnectionEpoch);
        var endExclusive = block.SequenceEndExclusive;

        lock (_gate)
        {
            if (_nextSequenceBySource.TryGetValue(key, out var expected))
            {
                if (block.SequenceStart > expected)
                {
                    var missing = block.SequenceStart - expected;
                    Interlocked.Add(ref _sequenceGapCount, missing);
                    RawRecorderTelemetry.SequenceGaps.Add(missing);
                }
                else if (block.SequenceStart < expected)
                {
                    if (endExclusive <= expected)
                    {
                        Interlocked.Increment(ref _duplicateBlockCount);
                        RawRecorderTelemetry.DuplicateBlocks.Add(1);
                    }
                    else
                    {
                        Interlocked.Increment(ref _outOfOrderBlockCount);
                        RawRecorderTelemetry.OutOfOrderBlocks.Add(1);
                    }
                }

                _nextSequenceBySource[key] = Math.Max(expected, endExclusive);
            }
            else
            {
                _nextSequenceBySource[key] = endExclusive;
            }

            var lastSequence = endExclusive - 1;
            if (!_lastSequenceBySource.TryGetValue(key, out var currentLast) ||
                lastSequence > currentLast)
            {
                _lastSequenceBySource[key] = lastSequence;
            }
        }
    }

    private async Task PreflightWriterAsync(CancellationToken cancellationToken)
    {
        var sessionDirectory = _sessionDirectory
            ?? throw new InvalidOperationException("Raw session directory is not initialized.");
        var probePath = Path.Combine(sessionDirectory, ".raw-writer-preflight.arrow.partial");
        Stream? stream = null;
        ArrowStreamWriter? writer = null;

        try
        {
            stream = _streamFactory.OpenWrite(probePath);
            writer = new ArrowStreamWriter(
                stream,
                SegmentWriter.RecordSchema,
                leaveOpen: true);
            await writer.WriteStartAsync(cancellationToken).ConfigureAwait(false);
            await writer.WriteEndAsync(cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writer?.Dispose();
            if (stream is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else
                stream?.Dispose();

            if (File.Exists(probePath))
                File.Delete(probePath);
        }
    }

    private void Accepted(CanonicalRawBlock block)
    {
        lock (_gate)
        {
            if (_state == RawRecorderState.Ready)
                _state = RawRecorderState.Running;
        }

        Interlocked.Increment(ref _acceptedBlocks);
        var depth = Interlocked.Increment(ref _queueDepth);
        UpdateHighWater(depth);
        RawRecorderTelemetry.AcceptedBlocks.Add(1);
        RawRecorderTelemetry.AcceptedBytes.Add(block.Payload.Length);
    }

    private async Task WriterLoopAsync(ChannelReader<CanonicalRawBlock> reader)
    {
        try
        {
            await foreach (var block in reader.ReadAllAsync().ConfigureAwait(false))
            {
                _slots!.Release();
                Interlocked.Decrement(ref _queueDepth);
                await WriteBlockAsync(block).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Fail(new RawRecorderFault(
                "raw.writer.failure",
                "Raw recorder writer failed.",
                ex,
                _timeProvider.GetUtcNow()));
            throw;
        }
    }

    private async Task WriteBlockAsync(CanonicalRawBlock block)
    {
        var ordinal = Interlocked.Increment(ref _writeOrdinal);
        if (ordinal == 1 || ordinal % _options.FreeSpaceCheckIntervalBlocks == 0)
            EnsureDiskSpace(_sessionDirectory!);

        var key = new SegmentKey(block.SourceId, block.ConnectionEpoch);
        if (!_activeSegments.TryGetValue(key, out var segment) ||
            segment.ShouldRotate(block.Payload.Length, _timeProvider.GetUtcNow()))
        {
            if (segment is not null)
            {
                await FinalizeSegmentAsync(segment, CancellationToken.None).ConfigureAwait(false);
                _activeSegments.Remove(key);
            }

            segment = await CreateSegmentAsync(key, CancellationToken.None).ConfigureAwait(false);
            _activeSegments.Add(key, segment);
        }

        var writeStarted = _timeProvider.GetTimestamp();
        await segment.WriteAsync(block).ConfigureAwait(false);
        RawRecorderTelemetry.WriteLatencyMs.Record(
            _timeProvider.GetElapsedTime(writeStarted).TotalMilliseconds);

        Interlocked.Increment(ref _writtenBlocks);
        Interlocked.Add(ref _writtenBytes, block.Payload.Length);
        Interlocked.Add(ref _sessionPayloadBytes, block.Payload.Length);
        RawRecorderTelemetry.WrittenBlocks.Add(1);
        RawRecorderTelemetry.WrittenBytes.Add(block.Payload.Length);
    }

    private async Task<SegmentWriter> CreateSegmentAsync(
        SegmentKey key,
        CancellationToken cancellationToken)
    {
        int index;
        lock (_gate)
        {
            index = _segments.Count(item =>
                string.Equals(item.SourceId, key.SourceId, StringComparison.Ordinal) &&
                item.ConnectionEpoch == key.ConnectionEpoch);
        }

        var sourcePart = SafePathPart(key.SourceId);
        var finalName = $"raw-{sourcePart}-e{key.ConnectionEpoch}-{index:D6}.arrow";
        var partialName = finalName + ".partial";
        var partialPath = Path.Combine(_sessionDirectory!, partialName);
        var finalPath = Path.Combine(_sessionDirectory!, finalName);

        var segment = await SegmentWriter.CreateAsync(
            partialPath,
            finalPath,
            key.SourceId,
            key.ConnectionEpoch,
            index,
            _options,
            _timeProvider.GetUtcNow(),
            _streamFactory,
            cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _segments.Add(new RawSegmentManifest(
                finalName,
                partialName,
                key.SourceId,
                key.ConnectionEpoch,
                index,
                "Active",
                0,
                0,
                null,
                null,
                null));
        }

        Interlocked.Increment(ref _segmentCount);
        await WriteManifestAsync("Running", null, cancellationToken).ConfigureAwait(false);
        return segment;
    }

    private async Task FinalizeActiveSegmentsAsync(CancellationToken cancellationToken)
    {
        foreach (var segment in _activeSegments.Values.ToArray())
            await FinalizeSegmentAsync(segment, cancellationToken).ConfigureAwait(false);

        _activeSegments.Clear();
    }

    private async Task FinalizeSegmentAsync(
        SegmentWriter segment,
        CancellationToken cancellationToken)
    {
        var finalizeStarted = _timeProvider.GetTimestamp();
        await segment.FinalizeAsync(_options.Durability, cancellationToken).ConfigureAwait(false);
        RawRecorderTelemetry.FinalizeLatencyMs.Record(
            _timeProvider.GetElapsedTime(finalizeStarted).TotalMilliseconds);

        if (_options.Durability != RawDurabilityLevel.Buffered)
        {
            Interlocked.Add(ref _flushedBlocks, segment.BlockCount);
            Interlocked.Add(ref _flushedBytes, segment.PayloadBytes);
            RawRecorderTelemetry.FlushedBlocks.Add(segment.BlockCount);
            RawRecorderTelemetry.FlushedBytes.Add(segment.PayloadBytes);
        }

        if (_options.Durability == RawDurabilityLevel.FlushToDiskOnFinalize)
        {
            Interlocked.Add(ref _durableBlocks, segment.BlockCount);
            Interlocked.Add(ref _durableBytes, segment.PayloadBytes);
            RawRecorderTelemetry.DurableBlocks.Add(segment.BlockCount);
            RawRecorderTelemetry.DurableBytes.Add(segment.PayloadBytes);
        }

        var fileHash = await ComputeFileHashAsync(segment.FinalPath, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            var index = _segments.FindIndex(item =>
                item.SourceId == segment.SourceId &&
                item.ConnectionEpoch == segment.ConnectionEpoch &&
                item.SegmentIndex == segment.SegmentIndex);
            if (index >= 0)
            {
                _segments[index] = _segments[index] with
                {
                    State = "Completed",
                    BlockCount = segment.BlockCount,
                    PayloadBytes = segment.PayloadBytes,
                    FirstSequence = segment.FirstSequence,
                    LastSequence = segment.LastSequence,
                    Sha256 = fileHash
                };
            }
        }

        await WriteManifestAsync(
            Snapshot.State == RawRecorderState.Stopping ? "Stopping" : "Running",
            null,
            cancellationToken).ConfigureAwait(false);
    }

    private void EnsureDiskSpace(string path)
    {
        var available = _storageSpaceProbe.GetAvailableFreeBytes(path);
        if (available is null)
            return;

        RawRecorderTelemetry.DiskAvailableBytes.Record(available.Value);
        if (available.Value < _options.HardMinimumFreeBytes)
            throw new IOException(
                $"Raw recorder free space {available.Value} is below hard threshold {_options.HardMinimumFreeBytes}.");

        if (available.Value < _options.WarningFreeBytes)
            RawRecorderTelemetry.LowDiskWarnings.Add(1);
    }

    private async Task WriteManifestAsync(
        string state,
        string? reason,
        CancellationToken cancellationToken)
    {
        await _manifestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RawSessionManifest manifest;
            string manifestPath;

            lock (_gate)
            {
                if (_manifestPath is null || _descriptor is null)
                    return;

                manifestPath = _manifestPath;
                manifest = new RawSessionManifest(
                    1,
                    Interlocked.Increment(ref _manifestGeneration),
                    "arrow-ipc-stream",
                    _descriptor.SessionId,
                    state,
                    _descriptor.StartedAt,
                    state is "Completed" or "Aborted" or "Faulted" or "Incomplete"
                        ? _timeProvider.GetUtcNow()
                        : null,
                    _descriptor.ConfigurationHash,
                    _options.Durability.ToString(),
                    _descriptor.Sources,
                    _segments.ToArray(),
                    Interlocked.Read(ref _sequenceGapCount),
                    Interlocked.Read(ref _duplicateBlockCount),
                    Interlocked.Read(ref _outOfOrderBlockCount),
                    reason);
            }

            var temp = manifestPath + ".tmp";
            await using (var stream = new FileStream(
                temp,
                new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                    BufferSize = 16 * 1024
                }))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    manifest,
                    RawManifestJson.Options,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (_options.Durability == RawDurabilityLevel.FlushToDiskOnFinalize &&
                    state is "Completed" or "Aborted" or "Faulted" or "Incomplete")
                {
                    stream.Flush(flushToDisk: true);
                }
            }

            File.Move(temp, manifestPath, overwrite: true);
        }
        finally
        {
            _manifestGate.Release();
        }
    }

    private async Task BestEffortManifestAsync(string state, string reason)
    {
        try
        {
            await WriteManifestAsync(state, reason, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The original storage failure remains authoritative.
        }
    }

    private void Fail(RawRecorderFault fault)
    {
        if (Interlocked.Exchange(ref _faultOnce, 1) != 0)
            return;

        lock (_gate)
        {
            _faultReason = fault.Message;
            _state = RawRecorderState.Faulted;
        }

        _queue?.Writer.TryComplete(fault.Exception ?? new IOException(fault.Message));
        try { _faultCancellation.Cancel(); } catch (ObjectDisposedException) { }
        RawRecorderTelemetry.Faults.Add(1);
        try
        {
            _faultObserver?.OnFault(fault);
        }
        catch
        {
            RawRecorderTelemetry.FaultObserverErrors.Add(1);
        }
        _faultManifestTask = BestEffortManifestAsync("Faulted", fault.Message);
    }

    private RawRecorderAcceptStatus CurrentRejectStatus() =>
        Snapshot.State == RawRecorderState.Faulted
            ? RawRecorderAcceptStatus.Faulted
            : RawRecorderAcceptStatus.Closed;

    private void UpdateHighWater(int depth)
    {
        while (true)
        {
            var current = Volatile.Read(ref _queueHighWater);
            if (depth <= current)
                return;
            if (Interlocked.CompareExchange(ref _queueHighWater, depth, current) == current)
                return;
        }
    }

    private static async Task<string> ComputeFileHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static string SafePathPart(string value)
    {
        var safe = new string(value
            .Where(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            .Take(32)
            .ToArray());
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..12];
        return string.IsNullOrWhiteSpace(safe) ? hash : $"{safe}-{hash}";
    }

    private static bool IsUnderRoot(string root, string candidate)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) +
            Path.DirectorySeparatorChar;
        return candidate.StartsWith(
            normalizedRoot,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    private sealed record SegmentKey(string SourceId, long ConnectionEpoch);

    internal sealed record RawSegmentManifest(
        string FileName,
        string PartialFileName,
        string SourceId,
        long ConnectionEpoch,
        int SegmentIndex,
        string State,
        long BlockCount,
        long PayloadBytes,
        long? FirstSequence,
        long? LastSequence,
        string? Sha256);

    internal sealed record RawSessionManifest(
        int SchemaVersion,
        long Generation,
        string Format,
        string SessionId,
        string State,
        DateTimeOffset StartedAt,
        DateTimeOffset? EndedAt,
        string ConfigurationHash,
        string Durability,
        IReadOnlyList<RawRecordingSourceIdentity> Sources,
        IReadOnlyList<RawSegmentManifest> Segments,
        long SequenceGapCount,
        long DuplicateBlockCount,
        long OutOfOrderBlockCount,
        string? Reason);


    private sealed class SegmentWriter : IAsyncDisposable
    {
        internal static readonly Schema RecordSchema = CreateSchema();
        private readonly Stream _stream;
        private readonly ArrowStreamWriter _writer;
        private readonly FileSystemRawRecorderOptions _options;
        private readonly DateTimeOffset _createdAt;
        private int _finalized;

        private SegmentWriter(
            Stream stream,
            ArrowStreamWriter writer,
            string partialPath,
            string finalPath,
            string sourceId,
            long connectionEpoch,
            int segmentIndex,
            FileSystemRawRecorderOptions options,
            DateTimeOffset createdAt)
        {
            _stream = stream;
            _writer = writer;
            PartialPath = partialPath;
            FinalPath = finalPath;
            SourceId = sourceId;
            ConnectionEpoch = connectionEpoch;
            SegmentIndex = segmentIndex;
            _options = options;
            _createdAt = createdAt;
        }

        public string PartialPath { get; }
        public string FinalPath { get; }
        public string SourceId { get; }
        public long ConnectionEpoch { get; }
        public int SegmentIndex { get; }
        public long BlockCount { get; private set; }
        public long PayloadBytes { get; private set; }
        public long? FirstSequence { get; private set; }
        public long? LastSequence { get; private set; }

        public static async Task<SegmentWriter> CreateAsync(
            string partialPath,
            string finalPath,
            string sourceId,
            long connectionEpoch,
            int segmentIndex,
            FileSystemRawRecorderOptions options,
            DateTimeOffset createdAt,
            IRawSegmentStreamFactory streamFactory,
            CancellationToken cancellationToken)
        {
            var stream = streamFactory.OpenWrite(partialPath);
            ArrowStreamWriter? writer = null;
            try
            {
                writer = new ArrowStreamWriter(stream, RecordSchema, leaveOpen: true);
                await writer.WriteStartAsync(cancellationToken).ConfigureAwait(false);
                return new SegmentWriter(
                    stream, writer, partialPath, finalPath, sourceId,
                    connectionEpoch, segmentIndex, options, createdAt);
            }
            catch
            {
                writer?.Dispose();
                if (stream is IAsyncDisposable asyncDisposable)
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                else
                    stream.Dispose();
                throw;
            }
        }

        public bool ShouldRotate(int nextPayloadBytes, DateTimeOffset now) =>
            BlockCount > 0 &&
            (PayloadBytes + nextPayloadBytes > _options.MaxSegmentBytes ||
             now - _createdAt >= _options.EffectiveMaxSegmentDuration);

        public async Task WriteAsync(CanonicalRawBlock block)
        {
            var checksum = SHA256.HashData(block.Payload.Span);
            using var batch = BuildBatch(block, checksum);
            await _writer.WriteRecordBatchAsync(batch).ConfigureAwait(false);

            BlockCount++;
            PayloadBytes += block.Payload.Length;
            FirstSequence ??= block.SequenceStart;
            LastSequence = block.SequenceEndExclusive - 1;
        }

        public async Task FinalizeAsync(
            RawDurabilityLevel durability,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _finalized, 1) != 0)
                return;

            await _writer.WriteEndAsync(cancellationToken).ConfigureAwait(false);
            if (durability != RawDurabilityLevel.Buffered)
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (durability == RawDurabilityLevel.FlushToDiskOnFinalize &&
                _stream is FileStream fileStream)
            {
                fileStream.Flush(flushToDisk: true);
            }

            _writer.Dispose();
            if (_stream is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else
                _stream.Dispose();
            File.Move(PartialPath, FinalPath, overwrite: false);
        }

        public async ValueTask DisposeAsync()
        {
            _writer.Dispose();
            if (_stream is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else
                _stream.Dispose();
        }

        private static Schema CreateSchema() =>
            new(
                [
                    new Field("source_id", StringType.Default, false),
                    new Field("device_id", StringType.Default, false),
                    new Field("connection_epoch", Int64Type.Default, false),
                    new Field("sequence_start", Int64Type.Default, false),
                    new Field("sequence_count", Int32Type.Default, false),
                    new Field("sample_count", Int32Type.Default, false),
                    new Field("channel_count", Int32Type.Default, false),
                    new Field("channel_layout_id", StringType.Default, false),
                    new Field("numeric_representation", Int32Type.Default, false),
                    new Field("byte_order", Int32Type.Default, false),
                    new Field("sample_rate_hz", DoubleType.Default, false),
                    new Field("device_timestamp", Int64Type.Default, false),
                    new Field("host_monotonic_timestamp", Int64Type.Default, false),
                    new Field("wall_clock_unix_ms", Int64Type.Default, false),
                    new Field("quality_flags", Int64Type.Default, false),
                    new Field("protocol_version", StringType.Default, false),
                    new Field("canonicalization_version", StringType.Default, false),
                    new Field("configuration_hash", StringType.Default, false),
                    new Field("payload", BinaryType.Default, false),
                    new Field("payload_sha256", BinaryType.Default, false)
                ],
                System.Array.Empty<KeyValuePair<string, string>>());

        private static RecordBatch BuildBatch(
            CanonicalRawBlock block,
            byte[] checksum)
        {
            return new RecordBatch(
                RecordSchema,
                [
                    new StringArray.Builder().Append(block.SourceId).Build(),
                    new StringArray.Builder().Append(block.DeviceId ?? string.Empty).Build(),
                    new Int64Array.Builder().Append(block.ConnectionEpoch).Build(),
                    new Int64Array.Builder().Append(block.SequenceStart).Build(),
                    new Int32Array.Builder().Append(block.SequenceCount).Build(),
                    new Int32Array.Builder().Append(block.SampleCount).Build(),
                    new Int32Array.Builder().Append(block.ChannelCount).Build(),
                    new StringArray.Builder().Append(block.ChannelLayoutId).Build(),
                    new Int32Array.Builder().Append((int)block.NumericRepresentation).Build(),
                    new Int32Array.Builder().Append((int)block.ByteOrder).Build(),
                    new DoubleArray.Builder().Append(block.SampleRateHz).Build(),
                    new Int64Array.Builder().Append(block.DeviceTimestamp ?? long.MinValue).Build(),
                    new Int64Array.Builder().Append(block.HostMonotonicTimestamp).Build(),
                    new Int64Array.Builder().Append(block.WallClockTimestamp.ToUnixTimeMilliseconds()).Build(),
                    new Int64Array.Builder().Append(unchecked((long)block.QualityFlags)).Build(),
                    new StringArray.Builder().Append(block.ProtocolVersion).Build(),
                    new StringArray.Builder().Append(block.CanonicalizationVersion).Build(),
                    new StringArray.Builder().Append(block.ConfigurationHash).Build(),
                    new BinaryArray.Builder().Append(block.Payload.Span).Build(),
                    new BinaryArray.Builder().Append(checksum).Build()
                ],
                1);
        }
    }
}

internal static class RawRecorderTelemetry
{
    public static Counter<long> AcceptedBlocks { get; } =
        OpenDeviceStudioTelemetry.Meter.CreateCounter<long>("opendevicestudio.raw.accepted.blocks", "{block}");
    public static Counter<long> AcceptedBytes { get; } =
        OpenDeviceStudioTelemetry.Meter.CreateCounter<long>("opendevicestudio.raw.accepted.bytes", "By");
    public static Counter<long> WrittenBlocks { get; } =
        OpenDeviceStudioTelemetry.Meter.CreateCounter<long>("opendevicestudio.raw.written.blocks", "{block}");
    public static Counter<long> WrittenBytes { get; } =
        OpenDeviceStudioTelemetry.Meter.CreateCounter<long>("opendevicestudio.raw.written.bytes", "By");
    public static Counter<long> FlushedBlocks { get; } =
        OpenDeviceStudioTelemetry.Meter.CreateCounter<long>("opendevicestudio.raw.flushed.blocks", "{block}");
    public static Counter<long> FlushedBytes { get; } =
        OpenDeviceStudioTelemetry.Meter.CreateCounter<long>("opendevicestudio.raw.flushed.bytes", "By");
    public static Counter<long> DurableBlocks { get; } =
        OpenDeviceStudioTelemetry.Meter.CreateCounter<long>("opendevicestudio.raw.durable.blocks", "{block}");
    public static Counter<long> DurableBytes { get; } =
        OpenDeviceStudioTelemetry.Meter.CreateCounter<long>("opendevicestudio.raw.durable.bytes", "By");
    public static Counter<long> SequenceGaps { get; } =
        OpenDeviceStudioTelemetry.Meter.CreateCounter<long>("opendevicestudio.raw.sequence.gaps", "{sequence}");
    public static Counter<long> DuplicateBlocks { get; } =
        OpenDeviceStudioTelemetry.Meter.CreateCounter<long>("opendevicestudio.raw.sequence.duplicate_blocks", "{block}");
    public static Counter<long> OutOfOrderBlocks { get; } =
        OpenDeviceStudioTelemetry.Meter.CreateCounter<long>("opendevicestudio.raw.sequence.out_of_order_blocks", "{block}");
    public static Histogram<double> WriteLatencyMs { get; } =
        OpenDeviceStudioTelemetry.Meter.CreateHistogram<double>("opendevicestudio.raw.write.latency", "ms");
    public static Histogram<double> FinalizeLatencyMs { get; } =
        OpenDeviceStudioTelemetry.Meter.CreateHistogram<double>("opendevicestudio.raw.finalize.latency", "ms");
    public static Histogram<long> DiskAvailableBytes { get; } =
        OpenDeviceStudioTelemetry.Meter.CreateHistogram<long>("opendevicestudio.raw.disk.available_bytes", "By");
    public static Counter<long> IntegrityErrors { get; } =
        OpenDeviceStudioTelemetry.Meter.CreateCounter<long>("opendevicestudio.raw.integrity.errors", "{error}");
    public static Counter<long> FaultObserverErrors { get; } =
        OpenDeviceStudioTelemetry.Meter.CreateCounter<long>("opendevicestudio.raw.fault_observer.errors", "{error}");
    public static Counter<long> LowDiskWarnings { get; } =
        OpenDeviceStudioTelemetry.Meter.CreateCounter<long>("opendevicestudio.raw.low_disk.warning", "{warning}");
    public static Counter<long> Faults { get; } =
        OpenDeviceStudioTelemetry.Meter.CreateCounter<long>("opendevicestudio.raw.faults", "{fault}");
    public static Counter<long> SessionsCompleted { get; } =
        OpenDeviceStudioTelemetry.Meter.CreateCounter<long>("opendevicestudio.raw.sessions.completed", "{session}");
}


internal interface IRawStorageSpaceProbe
{
    long? GetAvailableFreeBytes(string path);
}

internal sealed class DriveInfoRawStorageSpaceProbe : IRawStorageSpaceProbe
{
    public static DriveInfoRawStorageSpaceProbe Instance { get; } = new();

    private DriveInfoRawStorageSpaceProbe()
    {
    }

    public long? GetAvailableFreeBytes(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrWhiteSpace(root))
            return null;

        try
        {
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (DriveNotFoundException)
        {
            // Some virtual/provider file systems do not expose DriveInfo.
            return null;
        }
    }
}

internal interface IRawSegmentStreamFactory
{
    Stream OpenWrite(string path);
}

internal sealed class FileSystemRawSegmentStreamFactory : IRawSegmentStreamFactory
{
    public static FileSystemRawSegmentStreamFactory Instance { get; } = new();

    private FileSystemRawSegmentStreamFactory()
    {
    }

    public Stream OpenWrite(string path) =>
        new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = 64 * 1024
            });
}

