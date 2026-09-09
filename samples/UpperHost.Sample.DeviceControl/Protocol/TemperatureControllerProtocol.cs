using System.Globalization;
using System.Text;
using UpperHost.Abstractions.Protocols;
using UpperHost.Sample.DeviceControl.Domain;

namespace UpperHost.Sample.DeviceControl.Protocol;

public sealed class TemperatureControllerProtocol :
    IProtocolCodec<TemperatureControllerCommand, TemperatureControllerResponse>
{
    private readonly List<byte> _buffer = [];

    public ReadOnlyMemory<byte> Encode(TemperatureControllerCommand command)
    {
        var line = command switch
        {
            ReadStatusCommand => "READ",
            SetTargetTemperatureCommand set => $"SET_TARGET {set.Celsius.ToString(CultureInfo.InvariantCulture)}",
            SetRunningCommand { Running: true } => "START",
            SetRunningCommand => "STOP",
            _ => throw new NotSupportedException($"Unsupported command type: {command.GetType().Name}")
        };

        return Encoding.UTF8.GetBytes(line + "\n");
    }

    public void Append(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
            _buffer.Add(value);
    }

    public bool TryRead(out TemperatureControllerResponse? message)
    {
        var delimiter = _buffer.IndexOf((byte)'\n');
        if (delimiter < 0)
        {
            message = null;
            return false;
        }

        var payload = _buffer.GetRange(0, delimiter).ToArray();
        _buffer.RemoveRange(0, delimiter + 1);

        var line = Encoding.UTF8.GetString(payload).TrimEnd('\r');
        message = Parse(line);
        return true;
    }

    private static TemperatureControllerResponse Parse(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            throw new FormatException("Temperature controller returned an empty response.");

        var values = parts.Skip(1)
            .Select(part => part.Split('=', 2))
            .Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.OrdinalIgnoreCase);

        var success = parts[0].Equals("OK", StringComparison.OrdinalIgnoreCase);
        if (!values.TryGetValue("actual", out var actualText) ||
            !double.TryParse(actualText, NumberStyles.Float, CultureInfo.InvariantCulture, out var actual))
            throw new FormatException($"Response does not contain a valid actual temperature: '{line}'.");

        if (!values.TryGetValue("target", out var targetText) ||
            !double.TryParse(targetText, NumberStyles.Float, CultureInfo.InvariantCulture, out var target))
            throw new FormatException($"Response does not contain a valid target temperature: '{line}'.");

        if (!values.TryGetValue("running", out var runningText) ||
            !bool.TryParse(runningText, out var running))
            throw new FormatException($"Response does not contain a valid running flag: '{line}'.");

        values.TryGetValue("message", out var responseMessage);
        return new TemperatureControllerResponse(
            success,
            actual,
            target,
            running,
            responseMessage?.Replace('_', ' ') ?? (success ? "OK" : "Rejected"));
    }
}
