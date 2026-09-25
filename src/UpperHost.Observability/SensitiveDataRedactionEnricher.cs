using Serilog.Core;
using Serilog.Events;

namespace UpperHost.Observability;

internal sealed class SensitiveDataRedactionEnricher : ILogEventEnricher
{
    private const string Redacted = "[REDACTED]";

    private static readonly string[] SensitiveKeyFragments =
    [
        "password",
        "passwd",
        "token",
        "secret",
        "apikey",
        "api_key",
        "authorization",
        "credential",
        "privatekey",
        "private_key",
        "connectionstring",
        "connection_string"
    ];

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);

        foreach (var property in logEvent.Properties.ToArray())
        {
            if (!IsSensitive(property.Key))
                continue;

            logEvent.AddOrUpdateProperty(
                propertyFactory.CreateProperty(property.Key, Redacted));
        }
    }

    internal static bool IsSensitive(string propertyName) =>
        SensitiveKeyFragments.Any(fragment =>
            propertyName.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
