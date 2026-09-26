Issue: #58
Reason: Provide the default append-friendly Canonical RawData adapter without making Apache Arrow or file-system primitives part of the Core recorder contract.
Surface: OpenDeviceStudio.Storage.FileSystem adds typed FileSystemRawRecorderOptions, FileSystemRawRecorderFactory, bounded/dedicated FileSystemRawRecorder, Apache Arrow IPC segmented artifacts, exact artifact reading and report-only crash recovery scanning.
Surface: #58 recovery reporting includes sequence-integrity diagnostics, manifest-generation/uncommitted-temp evidence and recoverable-prefix scanning for truncated Arrow tails.
