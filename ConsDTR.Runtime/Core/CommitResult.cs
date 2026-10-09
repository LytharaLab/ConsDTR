namespace ConsDTR.Runtime;

public sealed record CommitResult(bool Accepted, string Reason, ProofEvent? Proof = null, ExactRecord? Record = null);
