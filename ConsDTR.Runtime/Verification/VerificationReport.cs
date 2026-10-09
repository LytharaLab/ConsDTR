namespace ConsDTR.Runtime;

public sealed record VerificationReport(bool SemanticVerified, bool RouteReplayVerified,
    bool ExecutionBound, string Reason, bool IsNegative, long VerificationWork)
{
    public bool Accepted => SemanticVerified && RouteReplayVerified && ExecutionBound;
}
