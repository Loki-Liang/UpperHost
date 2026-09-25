using Microsoft.Extensions.Options;

namespace UpperHost.Observability;

internal sealed class UpperHostObservabilityOptionsValidator :
    IValidateOptions<UpperHostObservabilityOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        UpperHostObservabilityOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ServiceName))
            failures.Add("UpperHost:Observability:ServiceName is required.");
        if (string.IsNullOrWhiteSpace(options.ServiceVersion))
            failures.Add("UpperHost:Observability:ServiceVersion is required.");

        ValidateFileLogging(options.FileLogging, failures);
        ValidateOtlp(options.Otlp, failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    internal static void ValidateAndThrow(UpperHostObservabilityOptions options)
    {
        var result = new UpperHostObservabilityOptionsValidator()
            .Validate(Options.DefaultName, options);
        if (result.Failed)
            throw new InvalidOperationException(string.Join(Environment.NewLine, result.Failures));
    }

    private static void ValidateFileLogging(
        UpperHostFileLoggingOptions options,
        ICollection<string> failures)
    {
        if (!Enum.IsDefined(options.MinimumLevel))
        {
            failures.Add(
                "UpperHost:Observability:Logging:File:MinimumLevel is not a supported enum value.");
        }

        if (!options.Enabled)
            return;

        if (string.IsNullOrWhiteSpace(options.Path))
        {
            failures.Add(
                "UpperHost:Observability:Logging:File:Path is required when file logging is enabled.");
        }
        else
        {
            try
            {
                _ = Path.GetFullPath(options.Path);
            }
            catch (Exception ex) when (
                ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                failures.Add(
                    "UpperHost:Observability:Logging:File:Path must be a valid filesystem path.");
            }
        }

        if (options.FileSizeLimitBytes <= 0)
        {
            failures.Add(
                "UpperHost:Observability:Logging:File:FileSizeLimitBytes must be greater than zero.");
        }

        if (options.RetainedFileCountLimit <= 0)
        {
            failures.Add(
                "UpperHost:Observability:Logging:File:RetainedFileCountLimit must be greater than zero.");
        }

        if (options.AsyncBufferSize <= 0)
        {
            failures.Add(
                "UpperHost:Observability:Logging:File:AsyncBufferSize must be greater than zero.");
        }
    }

    private static void ValidateOtlp(
        UpperHostOtlpOptions options,
        ICollection<string> failures)
    {
        if (double.IsNaN(options.TraceSampleRatio) ||
            double.IsInfinity(options.TraceSampleRatio) ||
            options.TraceSampleRatio <= 0 ||
            options.TraceSampleRatio > 1)
        {
            failures.Add(
                "UpperHost:Observability:Otlp:TraceSampleRatio must be greater than 0 and at most 1.");
        }

        if (!options.Enabled)
            return;

        if (string.IsNullOrWhiteSpace(options.Endpoint) ||
            !Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add(
                "UpperHost:Observability:Otlp:Endpoint must be an absolute HTTP or HTTPS URI when OTLP export is enabled.");
        }
    }
}
