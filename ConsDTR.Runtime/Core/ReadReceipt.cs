namespace ConsDTR.Runtime;

public sealed record ReadReceipt(Guid SessionId, long MicrostepId, long TransportSnapshotSeq,
    long SourceRecordId, long ValueRevision, long EdgeId, long TopologyRevision,
    SlotHandle Source, SlotHandle Receiver, long SourceBindingRevision,
    long ReceiverBindingRevision, long EdgeRevision, string Port, long ControlViewId,
    long ReceiptJournalSeq);
