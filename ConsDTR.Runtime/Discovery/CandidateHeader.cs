namespace ConsDTR.Runtime.Discovery;

/// <summary>Public identity metadata only; this is never a verified operand.</summary>
public sealed record CandidateHeader(
    int Slot,
    long Generation,
    long BindingRevision,
    long MetadataRevision,
    string ObjectId,
    string Bucket,
    bool IsTarget = false,
    bool Active = true);
