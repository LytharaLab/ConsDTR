using ConsDTR.Runtime;

namespace ConsDTR.Lib.Evaluation;

public sealed record DtrPairedComparisonReport(ExecutionMode Mode, string ObservedCorpusDigest,
    string LeftCheckpointDigest, string RightCheckpointDigest, string PairedBudgetSignature,
    DtrPairedEstimate Estimate, string LeftIntervention, string RightIntervention, string EvidenceScope);
