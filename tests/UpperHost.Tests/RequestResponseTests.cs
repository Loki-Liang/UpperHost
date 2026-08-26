using System.Text;
using UpperHost.Protocols;
using UpperHost.Transport.Simulator;

namespace UpperHost.Tests;

public sealed class RequestResponseTests
{
    [Fact]
    public async Task Request_response_works_over_simulator()
    {
        await using var transport = new SimulatorTransport();
        await transport.OpenAsync();

        transport.Sent += _ => transport.InjectAsync(Encoding.UTF8.GetBytes("PONG\n")).AsTask().GetAwaiter().GetResult();

        var codec = new LineProtocolCodec();
        var client = new RequestResponseClient<string, string>(transport, codec, codec);

        var response = await client.ExecuteAsync("PING", TimeSpan.FromSeconds(1));
        Assert.Equal("PONG", response);
    }
}
