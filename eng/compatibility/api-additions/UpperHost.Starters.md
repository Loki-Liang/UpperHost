Issue: #60
Reason: Expose the production bounded command dispatcher from the source-first composition root without creating a second command execution path.
Surface: UpperHost.Starters adds AddCommandDispatcher<TCommand,TResult> so a product Device/Command contract can opt into the host-owned UpperHost.Control dispatcher from its CompositionRoot.

Issue: #63
Reason: Expose the #63 state, polling and shared resource-arbitration runtimes from the source-first composition root.
Surface: UpperHost.Starters adds AddControlResourceArbitration, AddDeviceState<TState> and AddDevicePolling<TState> composition-root entry points.
