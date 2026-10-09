namespace ConsDTR.Runtime;

public sealed record WriteReceipt(Guid SessionId, long MicrostepId, long TransportSnapshotSeq,
    SlotHandle Worker, SlotHandle Target, long TargetRevisionBeforeWrite,
    long EdgeId, long TopologyRevision, string WritePort, long WorkerBindingRevision,
    long TargetBindingRevision, long EdgeRevision, long ControlViewId, long ReceiptJournalSeq);
