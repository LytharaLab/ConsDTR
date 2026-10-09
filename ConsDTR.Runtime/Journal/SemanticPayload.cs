namespace ConsDTR.Runtime;

internal sealed record SemanticPayload(KernelAction Action, bool Accepted, string Reason,
    ProofEvent? Proof, ExactRecord? Record, SlotState? Target);
