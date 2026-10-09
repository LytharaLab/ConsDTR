namespace ConsDTR.Runtime;

public sealed record RuntimeSnapshot(Guid SessionId, long MicrostepId, long TopologyRevision,
    long ExactStateRevision, IReadOnlyList<SlotState> Slots, IReadOnlyList<DataEdge> Edges,
    IReadOnlyList<ExactRecord> Records, IReadOnlyList<ProofEvent> Proofs, long RemainingMicrosteps, long RemainingWork, ExecutionMode Mode,
    long JournalBytes = 0, long JournalEvents = 0);
