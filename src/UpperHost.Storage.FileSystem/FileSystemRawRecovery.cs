using System.Security.Cryptography;
using System.Text.Json;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using UpperHost.Abstractions.Storage;

namespace UpperHost.Storage.FileSystem;

public static class FileSystemRawArtifactReader
{
    public static async Task<IReadOnlyList<CanonicalRawBlock>> ReadAllAsync(
        string sessionDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        var manifestPath = Path.Combine(sessionDirectory, "manifest.json");
        await using var manifestStream = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<FileSystemRawRecorder.RawSessionManifest>(
            manifestStream,
            cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Raw manifest is empty.");

        if (manifest.SchemaVersion != 1 ||
            !string.Equals(manifest.Format, "arrow-ipc-stream", StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"Unsupported Raw format {manifest.Format} schema {manifest.SchemaVersion}.");
        }

        var blocks = new List<CanonicalRawBlock>();
        foreach (var segment in manifest.Segments
                     .Where(static item => item.State == "Completed")
                     .OrderBy(static item => item.SourceId, StringComparer.Ordinal)
                     .ThenBy(static item => item.ConnectionEpoch)
                     .ThenBy(static item => item.SegmentIndex))
        {
            var path = Path.Combine(sessionDirectory, segment.FileName);
            blocks.AddRange(await ReadSegmentAsync(path, cancellationToken).ConfigureAwait(false));
        }

        return blocks;
    }

    internal static async Task<IReadOnlyList<CanonicalRawBlock>> ReadSegmentAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var blocks = new List<CanonicalRawBlock>();
        await using var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
        using var reader = new ArrowStreamReader(stream, leaveOpen: true);

        while (true)
        {
            var batch = await reader.ReadNextRecordBatchAsync(cancellationToken).ConfigureAwait(false);
            if (batch is null)
                break;

            using (batch)
            {
                for (var row = 0; row < batch.Length; row++)
                    blocks.Add(ReadBlock(batch, row, path));
            }
        }

        return blocks;
    }

    internal static CanonicalRawBlock ReadBlock(RecordBatch batch, int row, string path)
    {
        var source = ((StringArray)batch.Column(0)).GetString(row)
            ?? throw new InvalidDataException($"Null source_id in {path}.");
        var device = ((StringArray)batch.Column(1)).GetString(row);
        var epoch = Required(((Int64Array)batch.Column(2)).GetValue(row), "connection_epoch", path);
        var sequence = Required(((Int64Array)batch.Column(3)).GetValue(row), "sequence_start", path);
        var sequenceCount = Required(((Int32Array)batch.Column(4)).GetValue(row), "sequence_count", path);
        var sampleCount = Required(((Int32Array)batch.Column(5)).GetValue(row), "sample_count", path);
        var channelCount = Required(((Int32Array)batch.Column(6)).GetValue(row), "channel_count", path);
        var layout = ((StringArray)batch.Column(7)).GetString(row)
            ?? throw new InvalidDataException($"Null channel_layout_id in {path}.");
        var numeric = Required(((Int32Array)batch.Column(8)).GetValue(row), "numeric_representation", path);
        var byteOrder = Required(((Int32Array)batch.Column(9)).GetValue(row), "byte_order", path);
        var sampleRate = Required(((DoubleArray)batch.Column(10)).GetValue(row), "sample_rate_hz", path);
        var deviceTimestamp = Required(((Int64Array)batch.Column(11)).GetValue(row), "device_timestamp", path);
        var hostTimestamp = Required(((Int64Array)batch.Column(12)).GetValue(row), "host_monotonic_timestamp", path);
        var wallClock = Required(((Int64Array)batch.Column(13)).GetValue(row), "wall_clock_unix_ms", path);
        var quality = Required(((Int64Array)batch.Column(14)).GetValue(row), "quality_flags", path);
        var protocol = ((StringArray)batch.Column(15)).GetString(row)
            ?? throw new InvalidDataException($"Null protocol_version in {path}.");
        var canonicalization = ((StringArray)batch.Column(16)).GetString(row)
            ?? throw new InvalidDataException($"Null canonicalization_version in {path}.");
        var configuration = ((StringArray)batch.Column(17)).GetString(row)
            ?? throw new InvalidDataException($"Null configuration_hash in {path}.");
        var payload = ((BinaryArray)batch.Column(18)).GetBytes(row).ToArray();
        var checksum = ((BinaryArray)batch.Column(19)).GetBytes(row).ToArray();

        if (!SHA256.HashData(payload).AsSpan().SequenceEqual(checksum))
            throw new InvalidDataException($"Raw payload checksum mismatch in {path}.");

        return CanonicalRawBlock.CopyFrom(
            source,
            string.IsNullOrEmpty(device) ? null : device,
            epoch,
            sequence,
            sequenceCount,
            sampleCount,
            channelCount,
            layout,
            (RawNumericRepresentation)numeric,
            (RawByteOrder)byteOrder,
            sampleRate,
            deviceTimestamp == long.MinValue ? null : deviceTimestamp,
            hostTimestamp,
            DateTimeOffset.FromUnixTimeMilliseconds(wallClock),
            unchecked((ulong)quality),
            protocol,
            canonicalization,
            configuration,
            payload);
    }

    private static T Required<T>(T? value, string name, string path)
        where T : struct =>
        value ?? throw new InvalidDataException($"Null {name} in {path}.");
}

public static class FileSystemRawRecoveryScanner
{
    public static async Task<RawRecoveryReport> ScanAsync(
        string sessionDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        var issues = new List<RawRecoveryIssue>();
        var manifestPath = Path.Combine(sessionDirectory, "manifest.json");
        string? sessionId = null;
        var manifestState = "Missing";
        long verifiedBlocks = 0;
        long verifiedBytes = 0;

        FileSystemRawRecorder.RawSessionManifest? manifest = null;
        try
        {
            await using var manifestStream = File.OpenRead(manifestPath);
            manifest = await JsonSerializer.DeserializeAsync<FileSystemRawRecorder.RawSessionManifest>(
                manifestStream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            issues.Add(new RawRecoveryIssue(
                RawRecoveryIssueKind.CorruptSegment,
                manifestPath,
                $"Manifest could not be read: {ex.Message}"));
        }

        if (manifest is not null)
        {
            sessionId = manifest.SessionId;
            manifestState = manifest.State;

            if (manifest.SchemaVersion != 1 ||
                !string.Equals(manifest.Format, "arrow-ipc-stream", StringComparison.Ordinal))
            {
                issues.Add(new RawRecoveryIssue(
                    RawRecoveryIssueKind.UnsupportedFormat,
                    manifestPath,
                    $"Unsupported format {manifest.Format} schema {manifest.SchemaVersion}."));
            }

            if (!string.Equals(manifest.State, "Completed", StringComparison.Ordinal))
            {
                issues.Add(new RawRecoveryIssue(
                    RawRecoveryIssueKind.IncompleteSession,
                    manifestPath,
                    $"Session manifest state is {manifest.State}."));
            }
        }

        var segmentPaths = Directory
            .EnumerateFiles(sessionDirectory, "*.arrow*", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        foreach (var path in segmentPaths)
        {
            try
            {
                var blocks = await FileSystemRawArtifactReader
                    .ReadSegmentAsync(path, cancellationToken)
                    .ConfigureAwait(false);
                verifiedBlocks += blocks.Count;
                verifiedBytes += blocks.Sum(static block => (long)block.Payload.Length);
            }
            catch (Exception ex) when (
                ex is InvalidDataException
                    or EndOfStreamException
                    or IOException
                    or ArgumentOutOfRangeException)
            {
                issues.Add(new RawRecoveryIssue(
                    path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)
                        ? RawRecoveryIssueKind.TruncatedTail
                        : RawRecoveryIssueKind.CorruptSegment,
                    path,
                    $"Stopped at last complete Arrow batch: {ex.GetType().Name}: {ex.Message}"));
            }
        }

        if (manifest is not null)
        {
            foreach (var segment in manifest.Segments.Where(static item => item.State == "Completed"))
            {
                var path = Path.Combine(sessionDirectory, segment.FileName);
                if (!File.Exists(path))
                {
                    issues.Add(new RawRecoveryIssue(
                        RawRecoveryIssueKind.MissingSegment,
                        path,
                        "Completed manifest segment is missing."));
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(segment.Sha256))
                {
                    await using var stream = File.OpenRead(path);
                    var hash = Convert.ToHexString(
                        await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                    if (!string.Equals(hash, segment.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        issues.Add(new RawRecoveryIssue(
                            RawRecoveryIssueKind.ChecksumMismatch,
                            path,
                            "Segment SHA-256 does not match manifest."));
                    }
                }
            }
        }

        return new RawRecoveryReport(
            sessionDirectory,
            sessionId,
            manifestState,
            verifiedBlocks,
            verifiedBytes,
            issues);
    }
}
