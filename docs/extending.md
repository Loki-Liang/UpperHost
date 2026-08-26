# Extending UpperHost

## Add a device

Implement `IDevice`, then implement only the capabilities the hardware actually supports. Register the device in DI and `IDeviceRegistry` during application startup or from a module.

## Add a transport

Implement `ITransport`. Keep framing, checksums, retries that belong to the protocol, and domain commands out of the transport implementation.

## Add a protocol

Implement `ICommandEncoder<TCommand>` and/or `IMessageDecoder<TMessage>`. The decoder owns framing state, so it must correctly handle fragmented input and multiple messages in one received chunk.

## Add a workflow

Compose `IWorkflowStep` instances into a `WorkflowDefinition`. A workflow should describe product behavior such as connect -> self-test -> configure -> run -> stop, while each step delegates hardware details to capabilities/services.

## Add a plugin

Implement `IUpperHostModule` and register services inside `ConfigureServices`. Deploy the assembly to a trusted plugin directory and call `AddModulesFromDirectory` during bootstrap.

## Create an application

After installing the template package:

```powershell
dotnet new upperhost -n MyMachine --transport simulator
```

Supported baseline transport choices are `simulator`, `serial` and `tcp`. Additional providers should be delivered as separate packages/starters rather than added to the core assembly.
