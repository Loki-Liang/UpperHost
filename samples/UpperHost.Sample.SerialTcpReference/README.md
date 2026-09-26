# UpperHost Serial/TCP Reference

This sample is the shared protocol/domain reference for Issue #30. Serial and TCP use the same Reference Protocol and later differ only at the Transport/Provider edge.

## Reference Protocol v1

Binary frame:

```text
magic[2] = 55 48
version[1]
type[1]
payloadLength[2] little-endian
correlationId[4] little-endian
payload[N], N <= 4096
crc32[4] little-endian over header + payload
```

The protocol is a framework contract demonstration, not an industrial standard.

Serial/TCP byte streams are framed by `PipelinedFrameReader<T>` using `System.IO.Pipelines` internally. The public parser seam is `IFrameParser<T>`, based on `ReadOnlySequence<byte>`; `PipeReader`/`PipeWriter` do not leak into protocol contracts.

Current automated evidence is protocol-level only. TCP loopback, Serial fake-channel, reconnect and Control integration are added as separate Issue #30 commits. Real hardware status remains **NotRun** until an actual hardware validation record exists.
