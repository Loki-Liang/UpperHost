using System.IO.Ports;

namespace UpperHost.Starters;

public sealed class UpperHostTransportOptions
{
    public const string SectionName = "UpperHost:Transport";

    public string Type { get; set; } = "Simulator";
    public UpperHostSimulatorTransportOptions Simulator { get; set; } = new();
    public UpperHostSerialTransportOptions Serial { get; set; } = new();
    public UpperHostTcpTransportOptions Tcp { get; set; } = new();
    public UpperHostTransportResilienceOptions Resilience { get; set; } = new();
}

public sealed class UpperHostSimulatorTransportOptions
{
    public string Name { get; set; } = "default";
}

public sealed class UpperHostSerialTransportOptions
{
    public string? PortName { get; set; }
    public int BaudRate { get; set; } = 115200;
    public int DataBits { get; set; } = 8;
    public Parity Parity { get; set; } = Parity.None;
    public StopBits StopBits { get; set; } = StopBits.One;
    public int ReadBufferSize { get; set; } = 16 * 1024;
}

public sealed class UpperHostTcpTransportOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 9000;
    public int ReadBufferSize { get; set; } = 16 * 1024;
}

public sealed class UpperHostTransportResilienceOptions
{
    public bool Enabled { get; set; } = true;
    public int MaxAttempts { get; set; } = 5;
    public int InitialDelayMs { get; set; } = 250;
    public int MaximumDelayMs { get; set; } = 5000;
    public double BackoffFactor { get; set; } = 2.0;
    public bool ReconnectOnEndOfStream { get; set; } = true;
}
