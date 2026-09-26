using System.Buffers.Binary;
using System.Diagnostics;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;

const int blockCount = 2048;
const int payloadBytes = 4096;
const int batchSize = 64;

var blocks = Enumerable.Range(0, blockCount)
    .Select(index =>
    {
        var payload = new byte[payloadBytes];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)((index * 31 + i * 17) & 0xff);
        return new RawBlock(index, payload);
    })
    .ToArray();

var arrow = await MeasureAsync(() => WriteArrowAsync(blocks, batchSize));
var binary = await MeasureAsync(() => Task.FromResult(WriteMinimalBinary(blocks)));

await VerifyArrowAsync(arrow.Bytes, blocks);
VerifyMinimalBinary(binary.Bytes, blocks);

var truncatedArrow = arrow.Bytes.AsSpan(0, Math.Max(0, arrow.Bytes.Length - 512)).ToArray();
var recoverableArrowRows = await CountRecoverableArrowRowsAsync(truncatedArrow);
var recoverableBinaryRows = CountRecoverableBinaryRows(
    binary.Bytes.AsSpan(0, Math.Max(0, binary.Bytes.Length - 512)));

Console.WriteLine("UPPERHOST_RAW_FORMAT_SPIKE");
Console.WriteLine($"blocks={blockCount}; payload_bytes={payloadBytes}; logical_bytes={(long)blockCount * payloadBytes}");
Console.WriteLine($"arrow: bytes={arrow.Bytes.Length}; write_ms={arrow.Elapsed.TotalMilliseconds:F2}; allocated_bytes={arrow.AllocatedBytes}; truncated_recoverable_rows={recoverableArrowRows}");
Console.WriteLine($"minimal_binary: bytes={binary.Bytes.Length}; write_ms={binary.Elapsed.TotalMilliseconds:F2}; allocated_bytes={binary.AllocatedBytes}; truncated_recoverable_rows={recoverableBinaryRows}");
Console.WriteLine("decision-input: Arrow IPC provides a standard typed cross-language stream; minimal binary is smaller/simpler but would create a proprietary on-disk contract.");

if (recoverableArrowRows <= 0 || recoverableArrowRows >= blockCount)
    throw new InvalidOperationException("Arrow truncation probe did not expose a recoverable incomplete tail.");

if (recoverableBinaryRows <= 0 || recoverableBinaryRows >= blockCount)
    throw new InvalidOperationException("Minimal binary truncation probe did not expose a recoverable incomplete tail.");

static async Task<Measurement> MeasureAsync(Func<Task<byte[]>> action)
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    var before = GC.GetTotalAllocatedBytes(true);
    var started = Stopwatch.GetTimestamp();
    var bytes = await action().ConfigureAwait(false);
    var elapsed = Stopwatch.GetElapsedTime(started);
    var allocated = GC.GetTotalAllocatedBytes(true) - before;
    return new Measurement(bytes, elapsed, allocated);
}

static Schema CreateSchema() =>
    new(
        [
            new Field("sequence", Int64Type.Default, nullable: false),
            new Field("payload", BinaryType.Default, nullable: false)
        ],
        Array.Empty<KeyValuePair<string, string>>());

static async Task<byte[]> WriteArrowAsync(IReadOnlyList<RawBlock> blocks, int batchSize)
{
    var schema = CreateSchema();
    using var stream = new MemoryStream();
    using var writer = new ArrowStreamWriter(stream, schema, leaveOpen: true);
    await writer.WriteStartAsync().ConfigureAwait(false);

    for (var offset = 0; offset < blocks.Count; offset += batchSize)
    {
        var count = Math.Min(batchSize, blocks.Count - offset);
        var sequences = new Int64Array.Builder().Reserve(count);
        var payloads = new BinaryArray.Builder().Reserve(count);

        for (var i = 0; i < count; i++)
        {
            var block = blocks[offset + i];
            sequences.Append(block.Sequence);
            payloads.Append(block.Payload);
        }

        using var batch = new RecordBatch(
            schema,
            [
                sequences.Build(),
                payloads.Build()
            ],
            count);

        await writer.WriteRecordBatchAsync(batch).ConfigureAwait(false);
    }

    await writer.WriteEndAsync().ConfigureAwait(false);
    return stream.ToArray();
}

static async Task VerifyArrowAsync(byte[] bytes, IReadOnlyList<RawBlock> expected)
{
    using var reader = new ArrowStreamReader(bytes);
    var index = 0;

    while (true)
    {
        var batch = await reader.ReadNextRecordBatchAsync().ConfigureAwait(false);
        if (batch is null)
            break;

        using (batch)
        {
            var sequences = (Int64Array)batch.Column(0);
            var payloads = (BinaryArray)batch.Column(1);

            for (var row = 0; row < batch.Length; row++)
            {
                var sequence = sequences.GetValue(row)
                    ?? throw new InvalidDataException("Arrow sequence unexpectedly null.");
                var expectedBlock = expected[index++];

                if (sequence != expectedBlock.Sequence ||
                    !payloads.GetBytes(row).SequenceEqual(expectedBlock.Payload))
                {
                    throw new InvalidDataException("Arrow round-trip changed canonical raw bytes.");
                }
            }
        }
    }

    if (index != expected.Count)
        throw new InvalidDataException($"Arrow round-trip returned {index} rows; expected {expected.Count}.");
}

static async Task<int> CountRecoverableArrowRowsAsync(byte[] bytes)
{
    using var reader = new ArrowStreamReader(bytes);
    var rows = 0;

    try
    {
        while (true)
        {
            var batch = await reader.ReadNextRecordBatchAsync().ConfigureAwait(false);
            if (batch is null)
                break;

            using (batch)
                rows += batch.Length;
        }
    }
    catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException)
    {
    }

    return rows;
}

static byte[] WriteMinimalBinary(IReadOnlyList<RawBlock> blocks)
{
    using var stream = new MemoryStream();
    Span<byte> header = stackalloc byte[12];

    foreach (var block in blocks)
    {
        BinaryPrimitives.WriteInt64LittleEndian(header[..8], block.Sequence);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], block.Payload.Length);
        stream.Write(header);
        stream.Write(block.Payload);
    }

    return stream.ToArray();
}

static void VerifyMinimalBinary(byte[] bytes, IReadOnlyList<RawBlock> expected)
{
    var span = bytes.AsSpan();
    var offset = 0;

    foreach (var block in expected)
    {
        if (span.Length - offset < 12)
            throw new InvalidDataException("Minimal binary header truncated.");

        var sequence = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(offset, 8));
        var length = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset + 8, 4));
        offset += 12;

        if (length < 0 || span.Length - offset < length)
            throw new InvalidDataException("Minimal binary payload truncated.");

        if (sequence != block.Sequence ||
            !span.Slice(offset, length).SequenceEqual(block.Payload))
        {
            throw new InvalidDataException("Minimal binary round-trip changed canonical raw bytes.");
        }

        offset += length;
    }

    if (offset != span.Length)
        throw new InvalidDataException("Minimal binary contains unexpected trailing bytes.");
}

static int CountRecoverableBinaryRows(ReadOnlySpan<byte> bytes)
{
    var offset = 0;
    var rows = 0;

    while (bytes.Length - offset >= 12)
    {
        var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset + 8, 4));
        if (length < 0 || bytes.Length - offset - 12 < length)
            break;

        offset += 12 + length;
        rows++;
    }

    return rows;
}

internal sealed record RawBlock(long Sequence, byte[] Payload);
internal sealed record Measurement(byte[] Bytes, TimeSpan Elapsed, long AllocatedBytes);
