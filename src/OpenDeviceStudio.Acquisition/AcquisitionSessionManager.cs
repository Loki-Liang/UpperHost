using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace OpenDeviceStudio.Acquisition;

public sealed class AcquisitionSessionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, AcquisitionSession> _sessions = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly IReadOnlyList<IAcquisitionSessionDefinitionEnricher> _enrichers;
    private int _disposed;

    public AcquisitionSessionManager(TimeProvider? timeProvider = null)
        : this(timeProvider, null)
    {
    }

    internal AcquisitionSessionManager(
        TimeProvider? timeProvider,
        IEnumerable<IAcquisitionSessionDefinitionEnricher>? enrichers)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _enrichers = (enrichers ?? []).ToArray();
    }

    public int ActiveSessionCount => _sessions.Count;

    public AcquisitionSession CreateSession(AcquisitionSessionDefinition definition)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(definition);

        foreach (var enricher in _enrichers)
            definition = enricher.Enrich(definition);

        var session = new AcquisitionSession(definition, _timeProvider, OnSessionTerminal);
        if (!_sessions.TryAdd(session.SessionId, session))
            throw new InvalidOperationException($"An acquisition session with id '{session.SessionId}' is already registered.");

        return session;
    }

    public async Task<AcquisitionSession> StartSessionAsync(
        AcquisitionSessionDefinition definition,
        CancellationToken startRequestToken = default)
    {
        var session = CreateSession(definition);
        await session.StartAsync(startRequestToken).ConfigureAwait(false);
        return session;
    }

    public bool TryGetSession(string sessionId, out AcquisitionSession? session) =>
        _sessions.TryGetValue(sessionId, out session);

    public async Task<AcquisitionSessionResult?> StopSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return null;

        return await session.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAllAsync(CancellationToken hostShutdownToken = default)
    {
        var sessions = _sessions.Values.ToArray();
        if (sessions.Length == 0)
            return;

        try
        {
            await Task.WhenAll(
                    sessions.Select(session => session.HostShutdownAsync(hostShutdownToken)))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (hostShutdownToken.IsCancellationRequested)
        {
            await Task.WhenAll(
                    sessions
                        .Where(static session => !session.Completion.IsCompleted)
                        .Select(static session => session.AbortAsync(CancellationToken.None)))
                .ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var sessions = _sessions.Values.ToArray();
        await Task.WhenAll(
                sessions.Select(static session => session.AbortAsync(CancellationToken.None)))
            .ConfigureAwait(false);

        foreach (var session in sessions)
            await session.DisposeAsync().ConfigureAwait(false);

        _sessions.Clear();
    }

    private void OnSessionTerminal(AcquisitionSession session) =>
        _sessions.TryRemove(session.SessionId, out _);
}

public static class AcquisitionServiceCollectionExtensions
{
    public static IServiceCollection AddOpenDeviceStudioAcquisition(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(static descriptor =>
                descriptor.ServiceType == typeof(AcquisitionRegistrationMarker)))
            return services;

        services.AddSingleton<AcquisitionRegistrationMarker>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAcquisitionSessionDefinitionEnricher,
                RawRecordingSessionDefinitionEnricher>());
        services.TryAddSingleton(sp =>
            new AcquisitionSessionManager(
                sp.GetService<TimeProvider>() ?? TimeProvider.System,
                sp.GetServices<IAcquisitionSessionDefinitionEnricher>()));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, AcquisitionSessionManagerHostedService>());
        return services;
    }

    private sealed class AcquisitionRegistrationMarker;
}

internal sealed class AcquisitionSessionManagerHostedService(
    AcquisitionSessionManager manager) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) =>
        manager.StopAllAsync(cancellationToken);
}
