using ConsDTR.Runtime;
using ConsDTR.Runtime.Tasks;

namespace ConsDTR.Lib.Inference;

/// <summary>Shared operation heads with task-specific semantics; no task-specific neural weights.</summary>
public static class DtrActionVocabulary
{
    public static IReadOnlyList<string> ForTask(IKernelTask task) => task switch
    {
        HornTask => [.. Basic, HornTask.ApplyRule, HornTask.ProposeClosedSet, HornTask.ExtendClosedSet, HornTask.BeginClosedSetCheck,
            HornTask.CheckClosedSetNext, HornTask.FinalizeClosedSetCheck],
        ReachabilityTask => [.. Basic, ReachabilityTask.StartPath, ReachabilityTask.ExtendPath,
            ReachabilityTask.ProposeCut, ReachabilityTask.ExtendCut, ReachabilityTask.BeginCutCheck, ReachabilityTask.CheckCutNext, ReachabilityTask.FinalizeCutCheck],
        FiniteCspTask => [.. Basic, FiniteCspTask.ProposeAssignment, FiniteCspTask.ExtendAssignment, FiniteCspTask.CheckConstraint,
            FiniteCspTask.BeginAssignmentCheck, FiniteCspTask.CheckAssignmentNext, FiniteCspTask.FinalizeAssignmentCheck],
        _ => Basic
    };

    private static readonly string[] Basic = ["INPUT_REF", "COPY", "EQ", "COMPARE", "ADD", "SUB", "SELECT", "PROPOSE_VALUE"];
}
