namespace UpperHost.Abstractions.Protocols;

public interface ICommandEncoder<in TCommand>
{
    ReadOnlyMemory<byte> Encode(TCommand command);
}

public interface IMessageDecoder<TMessage>
{
    void Append(ReadOnlySpan<byte> bytes);
    bool TryRead(out TMessage? message);
}

public interface IProtocolCodec<in TCommand, TMessage> : ICommandEncoder<TCommand>, IMessageDecoder<TMessage>
{
}
