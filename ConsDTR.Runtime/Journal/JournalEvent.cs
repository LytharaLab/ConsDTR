namespace ConsDTR.Runtime;

public sealed record JournalEvent(long EventSeq, Guid SessionId, long MicrostepId,
    string Phase, string EventKind, string CanonicalPayload, long PreTopologyRevision,
    long PostTopologyRevision, long PreExactStateRevision, long PostExactStateRevision,
    string PreStateDigest, string PostStateDigest, string PrevEventDigest, string EventDigest,
    long DeltaWorkCost, long CumulativeWorkCost, ExecutionMode ExecutionMode,
    long OperationWorkCost)
{
    public string ComputeDigest() => DtrCanonical.Digest(this with { EventDigest = string.Empty });
}
