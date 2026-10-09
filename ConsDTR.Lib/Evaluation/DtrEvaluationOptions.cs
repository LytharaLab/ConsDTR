using ConsDTR.Lib.Inference;
using ConsDTR.Runtime.Tasks;

namespace ConsDTR.Lib.Evaluation;

public sealed record DtrEvaluationOptions
{
    public DtrInferenceOptions Inference { get; init; } = new();
    public string DataSplitFingerprint { get; init; } = string.Empty;
    public string? CheckpointId { get; init; }
    public IReadOnlyList<int> CallerDeclaredTrainingSeeds { get; init; } = Array.Empty<int>();
    public DtrBootstrapOptions Bootstrap { get; init; } = new();
    public int MaximumCases { get; init; } = 100_000;
    public Func<DtrEvaluationCase, Action<long>, TaskExecutionPlan>? ReferencePlanFactory { get; init; }
    public Func<DtrEvaluationCase, int, string?>? JournalPathFactory { get; init; }
}
