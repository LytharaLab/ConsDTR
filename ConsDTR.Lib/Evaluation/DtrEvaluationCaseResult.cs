using ConsDTR.Lib.Inference;
using ConsDTR.Runtime;

namespace ConsDTR.Lib.Evaluation;

public sealed record DtrEvaluationCaseResult(string CaseId, Guid SessionId, string TaskId,
    string TaskSemanticVersion, string TaskDefinitionDigest, string Split, string Family,
    int Scale, int StructureSeed, ExecutionMode Mode, DtrResultStatus Status,
    bool Certified, bool? OracleIsSatisfiable, string Reason, long Microsteps, long WorkCost,
    long? DirectoryScannedEntries, long EditedEdges, long RejectedActions, long ProofCount,
    long? JournalBytes, double RunElapsedMilliseconds, double EvaluationVerificationMilliseconds,
    long EvaluationVerificationWork, string? CertificateDigest, ulong SamplingSeed = 1, long JournalEvents = 0)
{
    public bool IsCsp => TaskSemanticVersion == "FiniteCSP/v1";
    public bool PositiveCertified => Certified && Status == DtrResultStatus.VerifiedResult;
    public bool OracleLabelConflict => IsCsp && OracleIsSatisfiable == false && PositiveCertified;
}
