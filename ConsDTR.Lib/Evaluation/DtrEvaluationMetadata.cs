using ConsDTR.Lib.Inference;
using ConsDTR.Runtime;
using ConsDTR.Runtime.Discovery;

namespace ConsDTR.Lib.Evaluation;

/// <summary>BudgetContractTemplate neutralizes session identity/storage path; live runs generate their own identities.</summary>
public sealed record DtrEvaluationMetadata(string ArchitectureId, string SpecRevision,
    string DataSplitFingerprint, string ObservedCorpusDigest, string? CallerCheckpointId,
    string ModelCheckpointDigest, int ActualCheckpointCount, IReadOnlyList<int> CallerDeclaredTrainingSeeds,
    string TrainingSeedProvenance, string CostScheduleId, SessionOptions BudgetContractTemplate,
    DirectoryOptions DirectoryContract, string PairedBudgetSignature, DtrBootstrapOptions Bootstrap,
    DtrEditorMode EditorIntervention, DtrMessageMode MessageIntervention, bool Deterministic,
    ulong SamplingSeed, string SamplingSeedSchedule, double Exploration, bool PolicyInterventionsApplied, string EvidenceScope);
