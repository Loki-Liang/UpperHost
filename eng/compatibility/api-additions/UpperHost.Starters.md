Issue: #60
Reason: Expose the production bounded command dispatcher from the source-first composition root without creating a second command execution path.
Surface: UpperHost.Starters adds AddCommandDispatcher<TCommand,TResult> so a product Device/Command contract can opt into the host-owned UpperHost.Control dispatcher from its CompositionRoot.
