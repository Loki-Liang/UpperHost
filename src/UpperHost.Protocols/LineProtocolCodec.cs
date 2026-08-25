using System.Text;
using UpperHost.Abstractions.Protocols;

namespace UpperHost.Protocols;

public sealed class LineProtocolCodec : IProtocolCodec<string, string>
{
    private readonly List<byte> _buffer = [];
    private readonly Encoding _encoding;
    private readonly byte _delimiter;

    public LineProtocolCodec(Encoding? encoding = null, byte delimiter = (byte)'\n')
    {
        _encoding = encoding ?? Encoding.UTF8;
        _delimiter = delimiter;
    }

    public ReadOnlyMemory<byte> Encode(string command) => _encoding.GetBytes(command + (char)_delimiter);

    public void Append(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
            _buffer.Add(value);
    }

    public bool TryRead(out string? message)
    {
        var index = _buffer.IndexOf(_delimiter);
        if (index < 0)
        {
            message = null;
            return false;
        }

        var payload = _buffer.GetRange(0, index).ToArray();
        _buffer.RemoveRange(0, index + 1);

        if (payload.Length > 0 && payload[^1] == (byte)'\r')
            payload = payload[..^1];

        message = _encoding.GetString(payload);
        return true;
    }
}
