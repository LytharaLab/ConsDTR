using ConsDTR.Runtime;
using ConsDTR.Runtime.Discovery;

namespace ConsDTR.Lib.Inference;

public sealed record DtrInferenceResult(
    DtrResultStatus Status,
    string Reason,
    ExecutionMode Mode,
    RuntimeSnapshot Snapshot,
    RuntimeCertificate? Certificate = null,
    VerificationReport? Verification = null,
    ExposureReport? Exposure = null,
    long RejectedActions = 0,
    long AcceptedEdits = 0,
    double ElapsedMilliseconds = 0,
    EndAnchor? ExpectedEndAnchor = null,
    IReadOnlyList<JournalEvent>? Journal = null)
{
    public bool IsVerified => Status is DtrResultStatus.VerifiedResult or DtrResultStatus.VerifiedNegative;
}

public enum DtrResultStatus
{
    VerifiedResult, VerifiedNegative, Unknown, UnverifiedCandidate, Unsupported, ResourceOrNumericFailure
}
