namespace UpperHost.Abstractions.Diagnostics;

public enum AlarmSeverity
{
    Information,
    Warning,
    Error,
    Critical
}

public enum AlarmStatus
{
    Raised,
    Acknowledged
}

public sealed record Alarm(
    Guid Id,
    string Source,
    string Code,
    string Message,
    AlarmSeverity Severity,
    AlarmStatus Status,
    DateTimeOffset RaisedAt,
    DateTimeOffset? AcknowledgedAt = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public interface IAlarmService
{
    IReadOnlyCollection<Alarm> Active { get; }
    event Action<Alarm>? Changed;

    ValueTask<Alarm> RaiseAsync(
        string source,
        string code,
        string message,
        AlarmSeverity severity,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default);

    ValueTask<bool> AcknowledgeAsync(Guid id, CancellationToken cancellationToken = default);
    ValueTask<bool> ClearAsync(Guid id, CancellationToken cancellationToken = default);
}
