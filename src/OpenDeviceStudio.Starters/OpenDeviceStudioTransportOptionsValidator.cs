using System.IO.Ports;
using Microsoft.Extensions.Options;

namespace OpenDeviceStudio.Starters;

internal sealed class OpenDeviceStudioTransportOptionsValidator : IValidateOptions<OpenDeviceStudioTransportOptions>
{
    public ValidateOptionsResult Validate(string? name, OpenDeviceStudioTransportOptions options)
    {
        var failures = new List<string>();
        var type = options.Type?.Trim();

        if (string.IsNullOrWhiteSpace(type))
        {
            failures.Add("OpenDeviceStudio:Transport:Type is required.");
        }
        else
        {
            switch (type.ToLowerInvariant())
            {
                case "simulator":
                    if (string.IsNullOrWhiteSpace(options.Simulator.Name))
                        failures.Add("OpenDeviceStudio:Transport:Simulator:Name is required when Type=Simulator.");
                    break;
                case "serial":
                    ValidateSerial(options.Serial, failures);
                    break;
                case "tcp":
                    ValidateTcp(options.Tcp, failures);
                    break;
                default:
                    failures.Add(
                        "OpenDeviceStudio:Transport:Type must be one of Simulator, Serial or Tcp.");
                    break;
            }
        }

        ValidateResilience(options.Resilience, failures);
        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    internal static void ValidateAndThrow(OpenDeviceStudioTransportOptions options)
    {
        var result = new OpenDeviceStudioTransportOptionsValidator()
            .Validate(Options.DefaultName, options);
        if (result.Failed)
            throw new InvalidOperationException(string.Join(Environment.NewLine, result.Failures));
    }

    private static void ValidateSerial(
        OpenDeviceStudioSerialTransportOptions options,
        ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(options.PortName))
            failures.Add("OpenDeviceStudio:Transport:Serial:PortName is required when Type=Serial.");
        if (options.BaudRate <= 0)
            failures.Add("OpenDeviceStudio:Transport:Serial:BaudRate must be greater than zero.");
        if (options.DataBits is < 5 or > 8)
            failures.Add("OpenDeviceStudio:Transport:Serial:DataBits must be in range 5..8.");
        if (!Enum.IsDefined(options.Parity))
            failures.Add("OpenDeviceStudio:Transport:Serial:Parity is not a supported enum value.");
        if (!Enum.IsDefined(options.StopBits) || options.StopBits == StopBits.None)
        {
            failures.Add(
                "OpenDeviceStudio:Transport:Serial:StopBits must be a supported non-None value.");
        }
        if (options.ReadBufferSize <= 0)
            failures.Add("OpenDeviceStudio:Transport:Serial:ReadBufferSize must be greater than zero.");
    }

    private static void ValidateTcp(
        OpenDeviceStudioTcpTransportOptions options,
        ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(options.Host))
            failures.Add("OpenDeviceStudio:Transport:Tcp:Host is required when Type=Tcp.");
        if (options.Port is < 1 or > 65535)
            failures.Add("OpenDeviceStudio:Transport:Tcp:Port must be in range 1..65535.");
        if (options.ReadBufferSize <= 0)
            failures.Add("OpenDeviceStudio:Transport:Tcp:ReadBufferSize must be greater than zero.");
    }

    private static void ValidateResilience(
        OpenDeviceStudioTransportResilienceOptions options,
        ICollection<string> failures)
    {
        if (options.MaxAttempts < 0)
            failures.Add("OpenDeviceStudio:Transport:Resilience:MaxAttempts must be non-negative.");
        if (options.InitialDelayMs < 0)
            failures.Add("OpenDeviceStudio:Transport:Resilience:InitialDelayMs must be non-negative.");
        if (options.MaximumDelayMs < 0)
            failures.Add("OpenDeviceStudio:Transport:Resilience:MaximumDelayMs must be non-negative.");
        if (options.BackoffFactor < 1 ||
            double.IsNaN(options.BackoffFactor) ||
            double.IsInfinity(options.BackoffFactor))
        {
            failures.Add(
                "OpenDeviceStudio:Transport:Resilience:BackoffFactor must be a finite number greater than or equal to 1.");
        }
        if (options.MaximumDelayMs < options.InitialDelayMs)
        {
            failures.Add(
                "OpenDeviceStudio:Transport:Resilience:MaximumDelayMs must be greater than or equal to OpenDeviceStudio:Transport:Resilience:InitialDelayMs.");
        }
    }
}
