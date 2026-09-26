Issue: #56
Reason: Add the production per-branch bounded Stream Router required by the Acquisition route so Required and Optional consumers no longer share one global overflow/failure model.
Surface: OpenDeviceStudio.Dataflow adds StreamRouter lifecycle and topology sealing, per-branch delivery/overflow/failure/ordering contracts, explicit per-branch PublishResult/partial-delivery evidence, owned consumer runners, immutable/copy/retain-release ownership adapters, bounded queue diagnostics and metrics, drain/cancel completion, and Required fault propagation with Optional isolation.

Issue: #56
Reason: Close the remaining StreamRouter reliability and production-reference gaps after the OpenDeviceStudio rename without creating a second fan-out contract.
Surface: OpenDeviceStudio.Dataflow StreamRouter refines publish/dispose race handling, exact DropNewest/Latest behavior, unified telemetry and queue-depth/capacity diagnostics while the DataAcquisition sample switches from legacy FanOutHub to the production StreamRouter contract.
