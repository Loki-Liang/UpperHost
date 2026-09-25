using Microsoft.Extensions.Options;

namespace UpperHost.Starters;

public enum UpperHostTransportType
{
    Simulator,
    Serial,
    Tcp
}

public sealed class UpperHostTransportOptions
{
    public const string SectionName = "UpperHost:Transport";

    public UpperHostTransportType Type { get; set; } = UpperHostTransportType.Simulator;
    public UpperHostSimulatorOptions Simulator { get; set; } = new();
    public UpperHostSerialOptions Serial { get; set; } = new();
    public UpperHostTcpOptions Tcp { get; set; } = new();
    public UpperHostResilienceOptions Resilience { get; set; } = new();
}

public sealed class UpperHostSimulatorOptions
{
    public string Name { get; set; } = "default";
}

public sealed class UpperHostSerialOptions
{
    public string? PortName { get; set; }
    public int BaudRate { get; set; } = 115200;
}

public sealed class UpperHostTcpOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 9000;
}

public sealed class UpperHostResilienceOptions
{
    public bool Enabled { get; set; } = true;
    public int MaxAttempts { get; set; } = 5;
    public int InitialDelayMs { get; set; } = 250;
    public int MaximumDelayMs { get; set; } = 5000;
    public double BackoffFactor { get; set; } = 2.0;
    public bool ReconnectOnEndOfStream { get; set; } = true;
}

public sealed class UpperHostTransportOptionsValidator : IValidateOptions<UpperHostTransportOptions>
{
    public ValidateOptionsResult Validate(string? name, UpperHostTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (!Enum.IsDefined(options.Type))
            failures.Add($"{UpperHostTransportOptions.SectionName}:Type is unsupported.");

        if (options.Resilience.MaxAttempts < 0)
            failures.Add($"{UpperHostTransportOptions.SectionName}:Resilience:MaxAttempts must be greater than or equal to 0.");

        if (options.Resilience.InitialDelayMs < 0)
            failures.Add($"{UpperHostTransportOptions.SectionName}:Resilience:InitialDelayMs must be greater than or equal to 0.");

        if (options.Resilience.MaximumDelayMs < options.Resilience.InitialDelayMs)
        {
            failures.Add(
                $"{UpperHostTransportOptions.SectionName}:Resilience:MaximumDelayMs must be greater than or equal to {UpperHostTransportOptions.SectionName}:Resilience:InitialDelayMs.");
        }

        if (options.Resilience.BackoffFactor < 1)
            failures.Add($"{UpperHostTransportOptions.SectionName}:Resilience:BackoffFactor must be greater than or equal to 1.");

        switch (options.Type)
        {
            case UpperHostTransportType.Simulator:
                if (string.IsNullOrWhiteSpace(options.Simulator.Name))
                    failures.Add($"{UpperHostTransportOptions.SectionName}:Simulator:Name is required when Type is Simulator.");
                break;

            case UpperHostTransportType.Serial:
                if (string.IsNullOrWhiteSpace(options.Serial.PortName))
                    failures.Add($"{UpperHostTransportOptions.SectionName}:Serial:PortName is required when Type is Serial.");
                if (options.Serial.BaudRate <= 0)
                    failures.Add($"{UpperHostTransportOptions.SectionName}:Serial:BaudRate must be a positive integer.");
                break;

            case UpperHostTransportType.Tcp:
                if (string.IsNullOrWhiteSpace(options.Tcp.Host))
                    failures.Add($"{UpperHostTransportOptions.SectionName}:Tcp:Host is required when Type is Tcp.");
                if (options.Tcp.Port is < 1 or > 65535)
                    failures.Add($"{UpperHostTransportOptions.SectionName}:Tcp:Port must be in range 1..65535.");
                break;
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
