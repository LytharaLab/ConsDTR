namespace ConsDTR.Runtime;

public sealed record ProofEvent(long ProofId, Guid SessionId, string TaskSemanticsVersion,
    string Operation, IReadOnlyList<long> InputProofIds, IReadOnlyList<long> InputValueRevisions,
    string? RuleOrConstraintInputId, ExactValue ExactOutput, long OutputRevision,
    SlotHandle Worker, long TopologyRevision, IReadOnlyList<ReadReceipt> DataReadReceipts,
    WriteReceipt? WriteReceipt, SlotHandle TargetHandle, long TargetValueRevision,
    IReadOnlyList<SlotHandle> ControlReadSet, long CommitStep, string ModelManifestId,
    string InputDigest, ExecutionMode ExecutionMode, long CommitJournalSeq,
    long TransportSnapshotSeq, long TargetBindingRevision, long ControlViewId,
    ExactValue? ProposedCandidate = null);
