using Microsoft.Extensions.Time.Testing;
using UpperHost.Abstractions.Transports;
using UpperHost.Testing;
using UpperHost.Transport.Simulator;

namespace UpperHost.Tests;

public sealed class FaultProfileTests
{
    [Fact]
    public async Task Send_latency_uses_time_provider_and_is_deterministic()
    {
        var clock = new FakeTimeProvider();
        var inner = new SimulatorTransport();
        await using var transport = inner.UseFaultProfile(
            new TransportFaultProfile("latency", Seed: 17, SendLatency: TimeSpan.FromSeconds(5)),
            clock);

        await transport.OpenAsync();

        var send = transport.SendAsync(new byte[] { 0x01 }).AsTask();
        Assert.False(send.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(5));
        await send;
    }

    [Fact]
    public async Task Cancellation_interrupts_injected_io_latency()
    {
        var clock = new FakeTimeProvider();
        var inner = new SimulatorTransport();
        await using var transport = inner.UseFaultProfile(
            new TransportFaultProfile("cancel-io", ReceiveLatency: TimeSpan.FromMinutes(1)),
            clock);

        await transport.OpenAsync();
        await inner.InjectAsync(new byte[] { 0x01 });

        using var cancellation = new CancellationTokenSource();
        await using var enumerator = transport.ReceiveAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        var moveNext = enumerator.MoveNextAsync().AsTask();
        Assert.False(moveNext.IsCompleted);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moveNext);
    }

    [Fact]
    public async Task Timeout_send_failure_and_command_reject_are_reproducible()
    {
        await AssertSendFaultAsync(
            new TransportFaultProfile("timeout", Seed: 1, TimeoutEverySend: 1),
            FaultKind.SendTimeout);

        await AssertSendFaultAsync(
            new TransportFaultProfile("send-failure", Seed: 2, FailEverySend: 1),
            FaultKind.SendFailure);

        await AssertSendFaultAsync(
            new TransportFaultProfile("command-reject", Seed: 3, RejectEverySend: 1),
            FaultKind.CommandRejected);
    }

    [Fact]
    public async Task Receive_failure_and_timeout_are_reproducible()
    {
        await AssertReceiveFaultAsync(
            new TransportFaultProfile("receive-failure", Seed: 4, FailEveryReceive: 1),
            FaultKind.ReceiveFailure);

        await AssertReceiveFaultAsync(
            new TransportFaultProfile("receive-timeout", Seed: 5, TimeoutEveryReceive: 1),
            FaultKind.ReceiveTimeout);
    }

    [Fact]
    public async Task Partial_corrupt_and_drop_receive_faults_are_deterministic()
    {
        var partialInner = new SimulatorTransport();
        await using (var partial = partialInner.UseFaultProfile(
            new TransportFaultProfile("partial", MaxReceiveFragmentSize: 2)))
        {
            await partial.OpenAsync();
            await partialInner.InjectAsync(new byte[] { 1, 2, 3, 4, 5 });

            await using var enumerator = partial.ReceiveAsync().GetAsyncEnumerator();
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(new byte[] { 1, 2 }, enumerator.Current.ToArray());
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(new byte[] { 3, 4 }, enumerator.Current.ToArray());
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(new byte[] { 5 }, enumerator.Current.ToArray());
        }

        var corruptInner = new SimulatorTransport();
        var events = new List<FaultInjectionEvent>();
        await using (var corrupt = corruptInner.UseFaultProfile(
            new TransportFaultProfile("corrupt", Seed: 123, CorruptEveryReceive: 1)))
        {
            corrupt.FaultInjected += events.Add;
            await corrupt.OpenAsync();
            await corruptInner.InjectAsync(new byte[] { 0x10, 0x20, 0x30 });

            await using var enumerator = corrupt.ReceiveAsync().GetAsyncEnumerator();
            Assert.True(await enumerator.MoveNextAsync());
            Assert.NotEqual(new byte[] { 0x10, 0x20, 0x30 }, enumerator.Current.ToArray());
            Assert.Contains(events, item => item.Kind == FaultKind.CorruptedReceive && item.ProfileName == "corrupt");
        }

        var dropInner = new SimulatorTransport();
        await using var drop = dropInner.UseFaultProfile(
            new TransportFaultProfile("drop", DropEveryReceive: 2));

        await drop.OpenAsync();
        await dropInner.InjectAsync(new byte[] { 1 });
        await dropInner.InjectAsync(new byte[] { 2 });
        await dropInner.InjectAsync(new byte[] { 3 });

        await using var dropEnumerator = drop.ReceiveAsync().GetAsyncEnumerator();
        Assert.True(await dropEnumerator.MoveNextAsync());
        Assert.Equal(new byte[] { 1 }, dropEnumerator.Current.ToArray());
        Assert.True(await dropEnumerator.MoveNextAsync());
        Assert.Equal(new byte[] { 3 }, dropEnumerator.Current.ToArray());
    }

    [Fact]
    public async Task Disconnect_reconnect_failure_and_recovery_are_explicit()
    {
        var inner = new SimulatorTransport();
        await using var transport = inner.UseFaultProfile(
            new TransportFaultProfile(
                "disconnect-reconnect",
                Seed: 99,
                DisconnectAfterSend: 1,
                FailReconnectAttempts: 1));

        await transport.OpenAsync();

        var disconnect = await Assert.ThrowsAsync<FaultInjectedException>(
            async () => await transport.SendAsync(new byte[] { 0x01 }));
        Assert.Equal(FaultKind.Disconnect, disconnect.Kind);
        Assert.Equal(TransportState.Faulted, transport.State);
        Assert.Equal(TransportState.Closed, inner.State);

        var reconnect = await Assert.ThrowsAsync<FaultInjectedException>(
            () => transport.OpenAsync());
        Assert.Equal(FaultKind.ReconnectFailure, reconnect.Kind);
        Assert.Contains("disconnect-reconnect", reconnect.Message);
        Assert.Contains("seed 99", reconnect.Message);

        await transport.OpenAsync();
        Assert.Equal(TransportState.Open, transport.State);
    }

    [Fact]
    public async Task Disposal_during_faulted_reconnect_releases_inner_transport()
    {
        var inner = new SimulatorTransport();
        var transport = inner.UseFaultProfile(
            new TransportFaultProfile("dispose-fault", DisconnectAfterSend: 1, FailReconnectAttempts: 1));

        await transport.OpenAsync();
        await Assert.ThrowsAsync<FaultInjectedException>(
            async () => await transport.SendAsync(new byte[] { 0x01 }));
        await Assert.ThrowsAsync<FaultInjectedException>(() => transport.OpenAsync());

        await transport.DisposeAsync();

        Assert.Equal(TransportState.Closed, transport.State);
        Assert.Equal(TransportState.Closed, inner.State);
    }

    [Fact]
    public async Task Simulator_backpressure_is_bounded_without_sleep_based_timing()
    {
        await using var simulator = new SimulatorTransport("bounded", capacity: 1);
        await simulator.OpenAsync();

        await simulator.InjectAsync(new byte[] { 1 });
        var blockedWriter = simulator.InjectAsync(new byte[] { 2 }).AsTask();

        Assert.False(blockedWriter.IsCompleted);

        await using var enumerator = simulator.ReceiveAsync().GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(new byte[] { 1 }, enumerator.Current.ToArray());

        await blockedWriter;
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(new byte[] { 2 }, enumerator.Current.ToArray());
    }

    private static async Task AssertSendFaultAsync(TransportFaultProfile profile, FaultKind expectedKind)
    {
        var inner = new SimulatorTransport();
        await using var transport = inner.UseFaultProfile(profile);
        await transport.OpenAsync();

        var exception = await Assert.ThrowsAsync<FaultInjectedException>(
            async () => await transport.SendAsync(new byte[] { 0x01 }));

        Assert.Equal(expectedKind, exception.Kind);
        Assert.Equal(profile.Seed, exception.Seed);
        Assert.Contains(profile.Name, exception.Message);
        Assert.Contains(transport.Endpoint.Address, exception.Message);
    }

    private static async Task AssertReceiveFaultAsync(TransportFaultProfile profile, FaultKind expectedKind)
    {
        var inner = new SimulatorTransport();
        await using var transport = inner.UseFaultProfile(profile);
        await transport.OpenAsync();
        await inner.InjectAsync(new byte[] { 0x01 });

        await using var enumerator = transport.ReceiveAsync().GetAsyncEnumerator();
        var exception = await Assert.ThrowsAsync<FaultInjectedException>(
            async () => { await enumerator.MoveNextAsync(); });

        Assert.Equal(expectedKind, exception.Kind);
        Assert.Contains(profile.Name, exception.Message);
    }
}
