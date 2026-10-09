using ConsDTR.Runtime;

namespace ConsDTR.Lib.Evaluation;

public sealed record DtrEvaluationReport(DtrEvaluationMetadata Metadata,
    IReadOnlyList<DtrEvaluationCaseResult> Cases, IReadOnlyList<DtrEvaluationGroup> Groups,
    IReadOnlyList<DtrEvaluationGroup> OverallByMode)
{
    // JSON is a display/research artifact; floating-point statistics are not canonical runtime journal fields.
    public string Serialize() => DtrCanonical.Json(this);
}
