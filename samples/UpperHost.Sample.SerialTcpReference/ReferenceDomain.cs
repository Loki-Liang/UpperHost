namespace UpperHost.Sample.SerialTcpReference;

public enum ReferenceMessageType : byte
{
    Identity = 1,
    ReadState = 2,
    SetTarget = 3,
    Start = 4,
    Stop = 5,
    State = 6,
    Acknowledged = 7,
    Error = 8
}

public sealed record ReferenceCommand(
    ReferenceMessageType Type,
    uint CorrelationId,
    ReadOnlyMemory<byte> Payload);

public sealed record ReferenceFrame(
    byte Version,
    ReferenceMessageType Type,
    uint CorrelationId,
    byte[] Payload);
