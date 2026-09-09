namespace UpperHost.Sample.DeviceControl.Domain;

public abstract record TemperatureControllerCommand;

public sealed record ReadStatusCommand : TemperatureControllerCommand;

public sealed record SetTargetTemperatureCommand(double Celsius) : TemperatureControllerCommand;

public sealed record SetRunningCommand(bool Running) : TemperatureControllerCommand;

public sealed record TemperatureControllerResponse(
    bool Success,
    double ActualCelsius,
    double TargetCelsius,
    bool Running,
    string Message);
