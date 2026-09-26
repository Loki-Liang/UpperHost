using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using UpperHost.Abstractions.Protocols;
using UpperHost.Protocols;

namespace UpperHost.Sample.SerialTcpReference;

/// <summary>
/// Small versioned protocol used only to demonstrate UpperHost transport/protocol contracts.
/// It is not an industrial standard protocol.
/// </summary>
public sealed class ReferenceProtocol :
    ICommandEncoder<ReferenceCommand>,
    IFrameParser<ReferenceFrame>
{
    public const byte CurrentVersion = 1;
    public const int MaxPayloadLength = 4 * 1024;
    public const int HeaderLength = 10;
    public const int ChecksumLength = 4;
    public const byte Magic0 = 0x55;
    public const byte Magic1 = 0x48;

    public ReadOnlyMemory<byte> Encode(ReferenceCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Payload.Length > MaxPayloadLength)
            throw new ArgumentOutOfRangeException(
                nameof(command),
                $"Reference payload exceeds {MaxPayloadLength} bytes.");

        var totalLength = checked(HeaderLength + command.Payload.Length + ChecksumLength);
        var frame = new byte[totalLength];
        var span = frame.AsSpan();

        span[0] = Magic0;
        span[1] = Magic1;
        span[2] = CurrentVersion;
        span[3] = (byte)command.Type;
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(4, 2), checked((ushort)command.Payload.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(6, 4), command.CorrelationId);
        command.Payload.Span.CopyTo(span.Slice(HeaderLength, command.Payload.Length));

        var checksumOffset = totalLength - ChecksumLength;
        var checksum = Crc32.HashToUInt32(span[..checksumOffset]);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(checksumOffset, ChecksumLength), checksum);
        return frame;
    }

    public bool TryParse(
        ref ReadOnlySequence<byte> buffer,
        out ReferenceFrame? frame)
    {
        frame = null;

        while (true)
        {
            if (!SeekMagic(ref buffer))
                return false;

            if (buffer.Length < HeaderLength)
                return false;

            Span<byte> header = stackalloc byte[HeaderLength];
            buffer.Slice(0, HeaderLength).CopyTo(header);

            if (header[2] != CurrentVersion)
            {
                buffer = buffer.Slice(1);
                continue;
            }

            var payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(4, 2));
            if (payloadLength > MaxPayloadLength)
            {
                buffer = buffer.Slice(1);
                continue;
            }

            var frameLength = HeaderLength + payloadLength + ChecksumLength;
            if (buffer.Length < frameLength)
                return false;

            var encoded = buffer.Slice(0, frameLength).ToArray();
            var checksumOffset = frameLength - ChecksumLength;
            var expected = BinaryPrimitives.ReadUInt32LittleEndian(
                encoded.AsSpan(checksumOffset, ChecksumLength));
            var actual = Crc32.HashToUInt32(encoded.AsSpan(0, checksumOffset));
            if (expected != actual)
            {
                buffer = buffer.Slice(1);
                continue;
            }

            frame = new ReferenceFrame(
                encoded[2],
                (ReferenceMessageType)encoded[3],
                BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(6, 4)),
                encoded.AsSpan(HeaderLength, payloadLength).ToArray());
            buffer = buffer.Slice(frameLength);
            return true;
        }
    }

    private static bool SeekMagic(ref ReadOnlySequence<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            var reader = new SequenceReader<byte>(buffer);
            if (!reader.TryRead(out var first))
                return false;

            if (first != Magic0)
            {
                buffer = buffer.Slice(reader.Position);
                continue;
            }

            if (!reader.TryPeek(out var second))
                return false;

            if (second == Magic1)
                return true;

            buffer = buffer.Slice(reader.Position);
        }

        return false;
    }
}
