using Microsoft.Extensions.Options;

namespace UpperHost.Observability;

public sealed class UpperHostObservabilityOptionsValidator : IValidateOptions<UpperHostObservabilityOptions>
{
    public ValidateOptionsResult Validate(string? name, UpperHostObservabilityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ServiceName))
            failures.Add($"{UpperHostObservabilityOptions.SectionName}:ServiceName is required.");

        if (string.IsNullOrWhiteSpace(options.ServiceVersion))
            failures.Add($"{UpperHostObservabilityOptions.SectionName}:ServiceVersion is required.");

        var file = options.Logging.File;
        if (file.Enabled)
        {
            if (string.IsNullOrWhiteSpace(file.Path))
                failures.Add($"{UpperHostObservabilityOptions.SectionName}:Logging:File:Path is required when file logging is enabled.");
            if (file.FileSizeLimitBytes <= 0)
                failures.Add($"{UpperHostObservabilityOptions.SectionName}:Logging:File:FileSizeLimitBytes must be greater than 0.");
            if (file.RetainedFileCountLimit <= 0)
                failures.Add($"{UpperHostObservabilityOptions.SectionName}:Logging:File:RetainedFileCountLimit must be greater than 0.");
            if (file.AsyncBufferSize <= 0)
                failures.Add($"{UpperHostObservabilityOptions.SectionName}:Logging:File:AsyncBufferSize must be greater than 0.");
        }

        var otlp = options.Otlp;
        if (otlp.TraceSampleRatio <= 0 || otlp.TraceSampleRatio > 1)
        {
            failures.Add(
                $"{UpperHostObservabilityOptions.SectionName}:Otlp:TraceSampleRatio must be greater than 0 and at most 1.");
        }

        if (otlp.Enabled &&
            (string.IsNullOrWhiteSpace(otlp.Endpoint) ||
             !Uri.TryCreate(otlp.Endpoint, UriKind.Absolute, out var endpoint) ||
             endpoint.Scheme is not ("http" or "https")))
        {
            failures.Add(
                $"{UpperHostObservabilityOptions.SectionName}:Otlp:Endpoint must be an absolute http/https URI when OTLP export is enabled.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
