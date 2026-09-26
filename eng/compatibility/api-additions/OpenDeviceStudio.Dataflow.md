Issue: #56
Reason: Add the production per-branch bounded Stream Router required by the Acquisition route so Required and Optional consumers no longer share one global overflow/failure model.
Surface: OpenDeviceStudio.Dataflow adds StreamRouter lifecycle and topology sealing, per-branch delivery/overflow/failure/ordering contracts, explicit per-branch PublishResult/partial-delivery evidence, owned consumer runners, immutable/copy/retain-release ownership adapters, bounded queue diagnostics and metrics, drain/cancel completion, and Required fault propagation with Optional isolation.
