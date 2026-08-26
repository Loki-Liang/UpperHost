using System.Runtime.CompilerServices;
using UpperHost.Abstractions.Transports;
using UpperHost.Resilience;

namespace UpperHost.Tests;

public sealed class ResilienceTests
{
    [Fact]
    public async Task Send_failure_reconnects_and_retries()
    {
        await using var transport = new ReconnectingTransport(
            new FlakyTransport(failFirstSend: true),
            new ReconnectPolicy(2, TimeSpan.Zero, 1, TimeSpan.Zero));

        await transport.OpenAsync();
        await transport.SendAsync(new byte[] { 1, 2, 3 });

        var inner = Assert.IsType<FlakyTransport>(GetInner(transport));
        Assert.Equal(2, inner.OpenCount);
        Assert.Equal(2, inner.SendCount);
    }

    [Fact]
    public async Task Receive_failure_reconnects_and_resumes_stream()
    {
        await using var transport = new ReconnectingTransport(
            new FlakyTransport(failFirstReceive: true),
            new ReconnectPolicy(2, TimeSpan.Zero, 1, TimeSpan.Zero));

        await transport.OpenAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var reader = transport.ReceiveAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(new byte[] { 42 }, reader.Current.ToArray());
    }

    private static ITransport GetInner(ReconnectingTransport transport)
    {
        var field = typeof(ReconnectingTransport).GetField("_inner", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return Assert.IsAssignableFrom<ITransport>(field!.GetValue(transport));
    }

    private sealed class FlakyTransport : ITransport
    {
        private readonly bool _failFirstSend;
        private readonly bool _failFirstReceive;
        private int _sendFailures;
        private int _receiveSessions;

        public FlakyTransport(bool failFirstSend = false, bool failFirstReceive = false)
        {
            _failFirstSend = failFirstSend;
            _failFirstReceive = failFirstReceive;
        }

        public TransportEndpoint Endpoint { get; } = new("test", "flaky");
        public TransportState State { get; private set; } = TransportState.Closed;
        public int OpenCount { get; private set; }
        public int SendCount { get; private set; }

        public Task OpenAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCount++;
            State = TransportState.Open;
            return Task.CompletedTask;
        }

        public Task CloseAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            State = TransportState.Closed;
            return Task.CompletedTask;
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SendCount++;
            if (_failFirstSend && Interlocked.Increment(ref _sendFailures) == 1)
                throw new IOException("Injected send failure.");
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = Interlocked.Increment(ref _receiveSessions);
            await Task.Yield();

            if (_failFirstReceive && session == 1)
                throw new IOException("Injected receive failure.");

            yield return new byte[] { 42 };
        }

        public ValueTask DisposeAsync()
        {
            State = TransportState.Closed;
            return ValueTask.CompletedTask;
        }
    }
}
