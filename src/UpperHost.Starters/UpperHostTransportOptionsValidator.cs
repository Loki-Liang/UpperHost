using Microsoft.Extensions.Options;

namespace UpperHost.Starters;

internal sealed class UpperHostTransportOptionsValidator : IValidateOptions<UpperHostTransportOptions>
{
    public ValidateOptionsResult Validate(string? name, UpperHostTransportOptions options)
    {
        var failures = new List<string>();
        var type = options.Type?.Trim();

        if (string.IsNullOrWhiteSpace(type))
        {
            failures.Add("UpperHost:Transport:Type is required.");
        }
        else
        {
            switch (type.ToLowerInvariant())
            {
                case "simulator":
                    if (string.IsNullOrWhiteSpace(options.Simulator.Name))
                        failures.Add("UpperHost:Transport:Simulator:Name is required when Type=Simulator.");
                    break;
                case "serial":
                    ValidateSerial(options.Serial, failures);
                    break;
                case "tcp":
                    ValidateTcp(options.Tcp, failures);
                    break;
                default:
                    failures.Add(
                        "UpperHost:Transport:Type must be one of Simulator, Serial or Tcp.");
                    break;
            }
        }

        ValidateResilience(options.Resilience, failures);
        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    internal static void ValidateAndThrow(UpperHostTransportOptions options)
    {
        var result = new UpperHostTransportOptionsValidator()
            .Validate(Options.DefaultName, options);
        if (result.Failed)
            throw new InvalidOperationException(string.Join(Environment.NewLine, result.Failures));
    }

    private static void ValidateSerial(
        UpperHostSerialTransportOptions options,
        ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(options.PortName))
            failures.Add("UpperHost:Transport:Serial:PortName is required when Type=Serial.");
        if (options.BaudRate <= 0)
            failures.Add("UpperHost:Transport:Serial:BaudRate must be greater than zero.");
        if (options.DataBits is < 5 or > 8)
            failures.Add("UpperHost:Transport:Serial:DataBits must be in range 5..8.");
        if (!Enum.IsDefined(options.Parity))
            failures.Add("UpperHost:Transport:Serial:Parity is not a supported enum value.");
        if (!Enum.IsDefined(options.StopBits))
            failures.Add("UpperHost:Transport:Serial:StopBits is not a supported enum value.");
        if (options.ReadBufferSize <= 0)
            failures.Add("UpperHost:Transport:Serial:ReadBufferSize must be greater than zero.");
    }

    private static void ValidateTcp(
        UpperHostTcpTransportOptions options,
        ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(options.Host))
            failures.Add("UpperHost:Transport:Tcp:Host is required when Type=Tcp.");
        if (options.Port is < 1 or > 65535)
            failures.Add("UpperHost:Transport:Tcp:Port must be in range 1..65535.");
        if (options.ReadBufferSize <= 0)
            failures.Add("UpperHost:Transport:Tcp:ReadBufferSize must be greater than zero.");
    }

    private static void ValidateResilience(
        UpperHostTransportResilienceOptions options,
        ICollection<string> failures)
    {
        if (options.MaxAttempts < 0)
            failures.Add("UpperHost:Transport:Resilience:MaxAttempts must be non-negative.");
        if (options.InitialDelayMs < 0)
            failures.Add("UpperHost:Transport:Resilience:InitialDelayMs must be non-negative.");
        if (options.MaximumDelayMs < 0)
            failures.Add("UpperHost:Transport:Resilience:MaximumDelayMs must be non-negative.");
        if (options.BackoffFactor < 1 || double.IsNaN(options.BackoffFactor))
            failures.Add("UpperHost:Transport:Resilience:BackoffFactor must be greater than or equal to 1.");
        if (options.MaximumDelayMs < options.InitialDelayMs)
        {
            failures.Add(
                "UpperHost:Transport:Resilience:MaximumDelayMs must be greater than or equal to UpperHost:Transport:Resilience:InitialDelayMs.");
        }
    }
}
