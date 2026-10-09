namespace ConsDTR.Runtime;

public sealed record ExactRecord(long RecordId, string InputId, ExactValue Value,
    long ValueRevision, long ProofId, bool IsInput);
