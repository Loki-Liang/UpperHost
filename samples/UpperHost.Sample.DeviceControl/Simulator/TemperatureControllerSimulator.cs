using System.Globalization;
using System.Text;
using UpperHost.Transport.Simulator;

namespace UpperHost.Sample.DeviceControl.Simulator;

public sealed class TemperatureControllerSimulator : IDisposable
{
    private readonly SimulatorTransport _transport;
    private double _actualCelsius = 22.5;
    private double _targetCelsius = 25.0;
    private bool _running;

    public TemperatureControllerSimulator(SimulatorTransport transport)
    {
        _transport = transport;
        _transport.Sent += OnSent;
    }

    private void OnSent(ReadOnlyMemory<byte> payload)
    {
        var command = Encoding.UTF8.GetString(payload.Span).Trim();
        var response = Execute(command);
        _transport.InjectAsync(Encoding.UTF8.GetBytes(response + "\n")).GetAwaiter().GetResult();
    }

    private string Execute(string command)
    {
        if (_running)
        {
            var delta = _targetCelsius - _actualCelsius;
            _actualCelsius += Math.Sign(delta) * Math.Min(Math.Abs(delta), 0.5);
        }

        if (command.Equals("READ", StringComparison.OrdinalIgnoreCase))
            return Status("OK", "status");

        if (command.Equals("START", StringComparison.OrdinalIgnoreCase))
        {
            _running = true;
            return Status("OK", "started");
        }

        if (command.Equals("STOP", StringComparison.OrdinalIgnoreCase))
        {
            _running = false;
            return Status("OK", "stopped");
        }

        const string prefix = "SET_TARGET ";
        if (command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var valueText = command[prefix.Length..];
            if (!double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return Status("ERR", "invalid_number");

            if (value is < 5 or > 95)
                return Status("ERR", "target_out_of_range");

            _targetCelsius = value;
            return Status("OK", "target_updated");
        }

        return Status("ERR", "unknown_command");
    }

    private string Status(string prefix, string message) => string.Create(
        CultureInfo.InvariantCulture,
        $"{prefix} actual={_actualCelsius:0.0} target={_targetCelsius:0.0} running={_running} message={message}");

    public void Dispose() => _transport.Sent -= OnSent;
}
