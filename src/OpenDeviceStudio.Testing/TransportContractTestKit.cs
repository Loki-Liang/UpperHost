using OpenDeviceStudio.Abstractions.Transports;

namespace OpenDeviceStudio.Testing;

public interface ITransportContractFixture : IAsyncDisposable
{
    string Name { get; }
    ITransport Transport { get; }

    ValueTask SendFromPeerAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
    ValueTask<ReadOnlyMemory<byte>> ReceiveFromTransportAsync(int expectedLength, CancellationToken cancellationToken = default);
    ValueTask AssertResourcesReleasedAsync(CancellationToken cancellationToken = default);
}

public sealed record TransportContractOptions(TimeSpan? CaseTimeout = null)
{
    public TimeSpan EffectiveCaseTimeout => CaseTimeout ?? TimeSpan.FromSeconds(5);
}

public sealed class TransportContractViolationException : Exception
{
    public TransportContractViolationException(
        string fixtureName,
        string caseName,
        TransportEndpoint endpoint,
        Exception innerException)
        : base($"Transport contract '{caseName}' failed for fixture '{fixtureName}' at {endpoint.Scheme}://{endpoint.Address}: {innerException.Message}", innerException)
    {
        FixtureName = fixtureName;
        CaseName = caseName;
        Endpoint = endpoint;
    }

    public string FixtureName { get; }
    public string CaseName { get; }
    public TransportEndpoint Endpoint { get; }
}

public static class TransportContractTestKit
{
    public static async Task VerifyAsync(
        Func<CancellationToken, ValueTask<ITransportContractFixture>> fixtureFactory,
        TransportContractOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixtureFactory);
        options ??= new TransportContractOptions();

        await VerifyCaseAsync("lifecycle-and-reconnect", fixtureFactory, options, VerifyLifecycleAsync, false, cancellationToken).ConfigureAwait(false);
        await VerifyCaseAsync("send-semantics", fixtureFactory, options, VerifySendAsync, false, cancellationToken).ConfigureAwait(false);
        await VerifyCaseAsync("receive-semantics", fixtureFactory, options, VerifyReceiveAsync, false, cancellationToken).ConfigureAwait(false);
        await VerifyCaseAsync("receive-cancellation", fixtureFactory, options, VerifyCancellationAsync, false, cancellationToken).ConfigureAwait(false);
        await VerifyCaseAsync("deterministic-disposal-and-ownership", fixtureFactory, options, VerifyDisposalAsync, true, cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyCaseAsync(
        string caseName,
        Func<CancellationToken, ValueTask<ITransportContractFixture>> fixtureFactory,
        TransportContractOptions options,
        Func<ITransportContractFixture, CancellationToken, Task> verify,
        bool verifyDisposesFixture,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.EffectiveCaseTimeout);

        ITransportContractFixture? fixture = null;
        try
        {
            fixture = await fixtureFactory(timeout.Token).ConfigureAwait(false);
            await verify(fixture, timeout.Token).ConfigureAwait(false);
        }
        catch (TransportContractViolationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var name = fixture?.Name ?? "<fixture-creation>";
            var endpoint = fixture?.Transport.Endpoint ?? new TransportEndpoint("unknown", "unknown");
            throw new TransportContractViolationException(name, caseName, endpoint, ex);
        }
        finally
        {
            if (fixture is not null && !verifyDisposesFixture)
                await fixture.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task VerifyLifecycleAsync(ITransportContractFixture fixture, CancellationToken cancellationToken)
    {
        Require(fixture.Transport.State == TransportState.Closed, $"Expected initial Closed state, got {fixture.Transport.State}.");

        await fixture.Transport.OpenAsync(cancellationToken).ConfigureAwait(false);
        Require(fixture.Transport.State == TransportState.Open, $"Expected Open state, got {fixture.Transport.State}.");

        await fixture.Transport.CloseAsync(cancellationToken).ConfigureAwait(false);
        Require(fixture.Transport.State == TransportState.Closed, $"Expected Closed state after close, got {fixture.Transport.State}.");

        await fixture.Transport.OpenAsync(cancellationToken).ConfigureAwait(false);
        Require(fixture.Transport.State == TransportState.Open, $"Expected Open state after reconnect, got {fixture.Transport.State}.");

        await fixture.Transport.CloseAsync(cancellationToken).ConfigureAwait(false);

        var failedClosedSend = false;
        try
        {
            await fixture.Transport.SendAsync(new byte[] { 0x01 }, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            failedClosedSend = true;
        }

        Require(failedClosedSend, "Send while closed must fail with InvalidOperationException.");
    }

    private static async Task VerifySendAsync(ITransportContractFixture fixture, CancellationToken cancellationToken)
    {
        await fixture.Transport.OpenAsync(cancellationToken).ConfigureAwait(false);

        var expected = new byte[] { 0x11, 0x22, 0x33, 0x44 };
        await fixture.Transport.SendAsync(expected, cancellationToken).ConfigureAwait(false);
        var actual = await fixture.ReceiveFromTransportAsync(expected.Length, cancellationToken).ConfigureAwait(false);

        Require(actual.Span.SequenceEqual(expected), $"Send payload mismatch. Expected {Convert.ToHexString(expected)}, got {Convert.ToHexString(actual.Span)}.");
    }

    private static async Task VerifyReceiveAsync(ITransportContractFixture fixture, CancellationToken cancellationToken)
    {
        await fixture.Transport.OpenAsync(cancellationToken).ConfigureAwait(false);

        var expected = new byte[] { 0x51, 0x52, 0x53, 0x54 };
        await fixture.SendFromPeerAsync(expected, cancellationToken).ConfigureAwait(false);

        await using var enumerator = fixture.Transport.ReceiveAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        Require(await enumerator.MoveNextAsync().ConfigureAwait(false), "Receive stream ended before peer payload arrived.");

        var actual = enumerator.Current;
        Require(actual.Span.SequenceEqual(expected), $"Receive payload mismatch. Expected {Convert.ToHexString(expected)}, got {Convert.ToHexString(actual.Span)}.");
    }

    private static async Task VerifyCancellationAsync(ITransportContractFixture fixture, CancellationToken cancellationToken)
    {
        await fixture.Transport.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var enumerator = fixture.Transport.ReceiveAsync(canceled.Token).GetAsyncEnumerator(canceled.Token);
        canceled.Cancel();

        var observedCancellation = false;
        try
        {
            await enumerator.MoveNextAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            observedCancellation = true;
        }

        Require(observedCancellation, "ReceiveAsync must honor cancellation instead of hanging or silently succeeding.");
    }

    private static async Task VerifyDisposalAsync(ITransportContractFixture fixture, CancellationToken cancellationToken)
    {
        await fixture.Transport.OpenAsync(cancellationToken).ConfigureAwait(false);
        await fixture.DisposeAsync().ConfigureAwait(false);

        Require(fixture.Transport.State == TransportState.Closed, $"Expected Closed state after disposal, got {fixture.Transport.State}.");
        await fixture.AssertResourcesReleasedAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
