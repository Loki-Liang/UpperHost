Issue: #60
Reason: Expose the production bounded command dispatcher from the source-first composition root without creating a second command execution path.
Surface: UpperHost.Starters adds AddCommandDispatcher<TCommand,TResult> so a product Device/Command contract can opt into the host-owned UpperHost.Control dispatcher from its CompositionRoot.

Issue: #64
Reason: Make the single Acquisition Session authority part of the source-first composition root and expose one explicit starter entrypoint without introducing a parallel acquisition lifecycle.
Surface: UpperHost.Starters defaults now register AcquisitionSessionManager and add AddAcquisitionRuntime() for product composition roots that opt into the runtime explicitly.

Issue: #58
Reason: Make Canonical RawData recording default-on in the source-first composition root while preserving an explicit audited opt-out.
Surface: UpperHost.Starters adds typed UpperHost:Acquisition:RawRecording options, startup validation, AddFileSystemRawRecording(), DisableRawRecording(reason), and configured default FileSystem recorder registration.
