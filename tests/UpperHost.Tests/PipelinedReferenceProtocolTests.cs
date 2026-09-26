using System.Threading.Channels;
using UpperHost.Abstractions.Transports;
using UpperHost.Protocols;
using UpperHost.Sample.SerialTcpReference;

namespace UpperHost.Tests;

public sealed class PipelinedReferenceProtocolTests
{
    private static readonly byte[] GoldenPing =
    [
        0x55, 0x48, 0x01, 0x01, 0x04, 0x00, 0x04, 0x03, 0x02, 0x01,
        0x50, 0x49, 0x4E, 0x47,
        0xAD, 0x3E, 0x53, 0x31
    ];

    [Fact]
    public void Reference_protocol_matches_independent_golden_vector()
    {
        var protocol = new ReferenceProtocol();

        var encoded = protocol.Encode(new ReferenceCommand(
            ReferenceMessageType.Identity,
            0x01020304,
            "PING"u8.ToArray()));

        Assert.Equal(GoldenPing, encoded.ToArray());

        var buffer = new System.Buffers.ReadOnlySequence<byte>(GoldenPing);
        Assert.True(protocol.TryParse(ref buffer, out var frame));
        Assert.NotNull(frame);
        Assert.Equal(ReferenceProtocol.CurrentVersion, frame!.Version);
        Assert.Equal(ReferenceMessageType.Identity, frame.Type);
        Assert.Equal(0x01020304u, frame.CorrelationId);
        Assert.Equal("PING"u8.ToArray(), frame.Payload);
        Assert.True(buffer.IsEmpty);
    }

    [Fact]
    public async Task Every_split_point_reassembles_one_frame()
    {
        for (var split = 1; split < GoldenPing.Length; split++)
        {
            await using var transport = new ChunkTransport();
            await transport.OpenAsync();
            var reader = new PipelinedFrameReader<ReferenceFrame>(
                transport,
                new ReferenceProtocol());

            await transport.InjectAsync(GoldenPing.AsMemory(0, split));
            await transport.InjectAsync(GoldenPing.AsMemory(split));
            transport.Complete();

            var frames = await ReadAllAsync(reader);

            var frame = Assert.Single(frames);
            Assert.Equal(0x01020304u, frame.CorrelationId);
            Assert.Equal("PING"u8.ToArray(), frame.Payload);
        }
    }

    [Fact]
    public async Task One_byte_reads_and_coalesced_frames_preserve_boundaries()
    {
        var protocol = new ReferenceProtocol();
        var second = protocol.Encode(new ReferenceCommand(
            ReferenceMessageType.ReadState,
            7,
            "STATE"u8.ToArray())).ToArray();

        await using var oneByteTransport = new ChunkTransport();
        await oneByteTransport.OpenAsync();
        var oneByteReader = new PipelinedFrameReader<ReferenceFrame>(
            oneByteTransport,
            new ReferenceProtocol());

        foreach (var value in GoldenPing)
            await oneByteTransport.InjectAsync([value]);
        oneByteTransport.Complete();

        Assert.Single(await ReadAllAsync(oneByteReader));

        await using var coalescedTransport = new ChunkTransport();
        await coalescedTransport.OpenAsync();
        var coalescedReader = new PipelinedFrameReader<ReferenceFrame>(
            coalescedTransport,
            new ReferenceProtocol());

        var combined = GoldenPing.Concat(second).ToArray();
        await coalescedTransport.InjectAsync(combined);
        coalescedTransport.Complete();

        var frames = await ReadAllAsync(coalescedReader);
        Assert.Equal(2, frames.Count);
        Assert.Equal(0x01020304u, frames[0].CorrelationId);
        Assert.Equal(7u, frames[1].CorrelationId);
    }

    [Fact]
    public async Task Garbage_corrupt_frame_and_oversize_length_resynchronize_to_next_valid_frame()
    {
        var corrupt = GoldenPing.ToArray();
        corrupt[^1] ^= 0xFF;

        byte[] oversizeHeader =
        [
            ReferenceProtocol.Magic0,
            ReferenceProtocol.Magic1,
            ReferenceProtocol.CurrentVersion,
            (byte)ReferenceMessageType.State,
            0xFF,
            0xFF,
            0x01, 0x00, 0x00, 0x00
        ];

        var stream = new byte[] { 0x00, 0x13, 0x37 }
            .Concat(corrupt)
            .Concat(oversizeHeader)
            .Concat(GoldenPing)
            .ToArray();

        await using var transport = new ChunkTransport();
        await transport.OpenAsync();
        var reader = new PipelinedFrameReader<ReferenceFrame>(
            transport,
            new ReferenceProtocol());

        await transport.InjectAsync(stream);
        transport.Complete();

        var frame = Assert.Single(await ReadAllAsync(reader));
        Assert.Equal(0x01020304u, frame.CorrelationId);
    }

    [Fact]
    public async Task Incomplete_tail_and_retained_buffer_limit_fail_explicitly()
    {
        await using var incomplete = new ChunkTransport();
        await incomplete.OpenAsync();
        var reader = new PipelinedFrameReader<ReferenceFrame>(
            incomplete,
            new ReferenceProtocol());

        await incomplete.InjectAsync(GoldenPing.AsMemory(0, GoldenPing.Length - 1));
        incomplete.Complete();

        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAllAsync(reader));

        var header = GoldenPing.Take(ReferenceProtocol.HeaderLength).ToArray();
        header[4] = 100;
        header[5] = 0;

        await using var oversizedRetention = new ChunkTransport();
        await oversizedRetention.OpenAsync();
        var boundedReader = new PipelinedFrameReader<ReferenceFrame>(
            oversizedRetention,
            new ReferenceProtocol(),
            new PipelinedFrameReaderOptions(
                MaxRetainedBytes: 32,
                PauseWriterThreshold: 64,
                ResumeWriterThreshold: 32));

        await oversizedRetention.InjectAsync(
            header.Concat(Enumerable.Repeat((byte)0xAA, 30)).ToArray());
        oversizedRetention.Complete();

        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAllAsync(boundedReader));
    }

    private static async Task<List<ReferenceFrame>> ReadAllAsync(
        PipelinedFrameReader<ReferenceFrame> reader)
    {
        var frames = new List<ReferenceFrame>();
        await foreach (var frame in reader.ReadAsync())
            frames.Add(frame);
        return frames;
    }

    private sealed class ChunkTransport : ITransport
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();

        public TransportEndpoint Endpoint { get; } = new("test", "pipelined");
        public TransportState State { get; private set; } = TransportState.Closed;

        public Task OpenAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            State = TransportState.Open;
            return Task.CompletedTask;
        }

        public Task CloseAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            State = TransportState.Closed;
            _chunks.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public ValueTask SendAsync(
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await foreach (var chunk in _chunks.Reader.ReadAllAsync(cancellationToken))
                yield return chunk;
        }

        public ValueTask InjectAsync(
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default) =>
            _chunks.Writer.WriteAsync(data.ToArray(), cancellationToken);

        public void Complete() => _chunks.Writer.TryComplete();

        public ValueTask DisposeAsync()
        {
            State = TransportState.Closed;
            _chunks.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
