using System.Collections.Immutable;

namespace ConsDTR.Runtime.Tasks;

/// <summary>Explicit algorithm/teacher identity. A plan is neither a proof nor a learned-only success.</summary>
public sealed record TaskExecutionPlan(ExecutionMode Mode, ImmutableArray<TaskPlannedAction> Actions,
    string OutputBinding, string CompletionReason);
