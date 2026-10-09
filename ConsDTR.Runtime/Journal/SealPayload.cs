namespace ConsDTR.Runtime;

internal sealed record SealPayload(ExactValue Output, string ProofRootDigest,
    ExecutionMode Mode, string ModelCheckpointDigest, long FinalGateAllowance);
