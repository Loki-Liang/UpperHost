using System.Collections.Concurrent;
using UpperHost.Abstractions.Diagnostics;

namespace UpperHost.Diagnostics;

public sealed class AlarmService : IAlarmService
{
    private readonly ConcurrentDictionary<Guid, Alarm> _active = new();

    public IReadOnlyCollection<Alarm> Active => _active.Values.OrderByDescending(x => x.RaisedAt).ToArray();

    public event Action<Alarm>? Changed;

    public ValueTask<Alarm> RaiseAsync(
        string source,
        string code,
        string message,
        AlarmSeverity severity,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var alarm = new Alarm(
            Guid.NewGuid(),
            source,
            code,
            message,
            severity,
            AlarmStatus.Raised,
            DateTimeOffset.UtcNow,
            null,
            metadata);

        _active[alarm.Id] = alarm;
        Changed?.Invoke(alarm);
        return ValueTask.FromResult(alarm);
    }

    public ValueTask<bool> AcknowledgeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_active.TryGetValue(id, out var alarm))
            return ValueTask.FromResult(false);

        var acknowledged = alarm with
        {
            Status = AlarmStatus.Acknowledged,
            AcknowledgedAt = DateTimeOffset.UtcNow
        };

        _active[id] = acknowledged;
        Changed?.Invoke(acknowledged);
        return ValueTask.FromResult(true);
    }

    public ValueTask<bool> ClearAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_active.TryRemove(id, out var alarm))
            return ValueTask.FromResult(false);

        Changed?.Invoke(alarm);
        return ValueTask.FromResult(true);
    }
}
