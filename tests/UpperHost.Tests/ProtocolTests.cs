using System.Text;
using UpperHost.Protocols;

namespace UpperHost.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void Line_decoder_handles_fragmented_input()
    {
        var codec = new LineProtocolCodec();
        codec.Append(Encoding.UTF8.GetBytes("HEL"));
        Assert.False(codec.TryRead(out _));

        codec.Append(Encoding.UTF8.GetBytes("LO\r\nNEXT\n"));
        Assert.True(codec.TryRead(out var first));
        Assert.Equal("HELLO", first);
        Assert.True(codec.TryRead(out var second));
        Assert.Equal("NEXT", second);
    }
}
