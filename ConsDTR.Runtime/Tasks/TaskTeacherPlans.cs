namespace ConsDTR.Runtime.Tasks;

/// <summary>Explicit diagnostic/teacher plans from supplied witnesses. These helpers perform no BFS or SAT search.</summary>
public static class TaskTeacherPlans
{
    public static TaskExecutionPlan PathWitness(GraphPathProblem problem, IEnumerable<string> edgeIds, Action<long> charge)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(edgeIds);
        ArgumentNullException.ThrowIfNull(charge);
        charge(1L + problem.Edges.Length);
        var byId = problem.Edges.ToDictionary(edge => edge.Id, StringComparer.Ordinal);
        var actions = System.Collections.Immutable.ImmutableArray.CreateBuilder<TaskPlannedAction>();
        actions.Add(new("path:0", ReachabilityTask.StartPath, ["query"]));
        var current = problem.Source;
        var seen = new HashSet<string>(StringComparer.Ordinal) { current };
        var last = "path:0";
        var index = 0;
        foreach (var edgeId in edgeIds)
        {
            charge(3);
            if (!byId.TryGetValue(edgeId, out var edge) || edge.Source != current || (problem.RequireSimplePath && !seen.Add(edge.Target)))
                throw new ArgumentException("The supplied edge identities do not form a valid task path.", nameof(edgeIds));
            var next = "path:" + (++index);
            actions.Add(new(next, ReachabilityTask.ExtendPath, [last, ReachabilityTask.EdgeInputId(edge.Id)]));
            current = edge.Target;
            last = next;
        }
        if (current != problem.Target)
            throw new ArgumentException("The supplied witness does not reach the query target.", nameof(edgeIds));
        return new(ExecutionMode.OracleDiagnostic, actions.ToImmutable(), last, "Explicit supplied path witness; no shortest-path claim.");
    }

    public static TaskExecutionPlan CutWitness(GraphPathProblem problem, IEnumerable<string> members, Action<long> charge)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(charge);
        charge(checked(1L + problem.Vertices.Length + problem.Edges.Length));
        var cut = TaskCandidates.GraphCut(problem, MeterMembers(members, charge));
        var actions = System.Collections.Immutable.ImmutableArray.CreateBuilder<TaskPlannedAction>();
        charge(cut.Data.Length + 2L);
        actions.Add(new("cut:candidate", ReachabilityTask.ProposeCut, [], cut));
        actions.Add(new("cut:0", ReachabilityTask.BeginCutCheck, ["cut:candidate"]));
        var last = "cut:0";
        for (var index = 0; index < problem.Edges.Length; index++)
        {
            charge(4);
            var next = "cut:" + (index + 1);
            actions.Add(new(next, ReachabilityTask.CheckCutNext, [last, "cut:candidate", ReachabilityTask.EdgeInputId(problem.Edges[index].Id)]));
            last = next;
        }
        charge(2);
        actions.Add(new("cut:answer", ReachabilityTask.FinalizeCutCheck, [last]));
        return new(ExecutionMode.OracleDiagnostic, actions.ToImmutable(), "cut:answer", "Explicit supplied cut witness; every input edge requires checked transport.");
    }

    public static TaskExecutionPlan AssignmentWitness(CspProblem problem, IEnumerable<KeyValuePair<string, int>> values, Action<long> charge)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(charge);
        charge(checked(1L + problem.Variables.Length + problem.Constraints.Length));
        var candidate = TaskCandidates.Assignment(problem, MeterEntries(values, charge));
        var actions = System.Collections.Immutable.ImmutableArray.CreateBuilder<TaskPlannedAction>();
        charge(candidate.Data.Length + 2L);
        actions.Add(new("assignment:candidate", FiniteCspTask.ProposeAssignment, [], candidate));
        actions.Add(new("assignment:0", FiniteCspTask.BeginAssignmentCheck, ["assignment:candidate"]));
        var last = "assignment:0";
        for (var index = 0; index < problem.Constraints.Length; index++)
        {
            charge(4);
            var next = "assignment:" + (index + 1);
            actions.Add(new(next, FiniteCspTask.CheckAssignmentNext,
                [last, "assignment:candidate", FiniteCspTask.ConstraintInputId(problem.Constraints[index].Id)]));
            last = next;
        }
        charge(2);
        actions.Add(new("assignment:answer", FiniteCspTask.FinalizeAssignmentCheck, [last]));
        return new(ExecutionMode.OracleDiagnostic, actions.ToImmutable(), "assignment:answer", "Explicit supplied assignment witness; every constraint requires checked transport; never UNSAT.");
    }

    private static IEnumerable<string> MeterMembers(IEnumerable<string> members, Action<long> charge)
    {
        foreach (var member in members) { charge(checked(1L + member.Length)); yield return member; }
    }
    private static IEnumerable<KeyValuePair<string, int>> MeterEntries(IEnumerable<KeyValuePair<string, int>> entries, Action<long> charge)
    {
        foreach (var entry in entries) { charge(checked(2L + entry.Key.Length)); yield return entry; }
    }
}
