namespace ConsDTR.Runtime.Tasks;

/// <summary>
/// Finite Horn reference closure scheduling. This explicit FairFallback never runs inside a semantic checker.
/// The caller must meter before each allocation/visit and execute the plan via the normal verified runtime.
/// Planning alone cannot certify an answer; failed routed proposals must abort the planned result.
/// </summary>
public static class HornFairScheduler
{
    public static TaskExecutionPlan BuildPlan(HornProblem problem, Action<long> charge)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(charge);
        charge(checked(1L + problem.Atoms.Length + problem.Facts.Length));
        var bindings = problem.Facts.ToDictionary(atom => atom, HornTask.FactInputId, StringComparer.Ordinal);
        var actions = System.Collections.Immutable.ImmutableArray.CreateBuilder<TaskPlannedAction>();
        void Add(TaskPlannedAction action)
        {
            charge(checked(1L + action.OperandBindings.Length + (action.Candidate?.Data.Length ?? 0)));
            actions.Add(action);
        }
        if (bindings.TryGetValue(problem.Query, out var inputAnswer))
            return new(ExecutionMode.FairFallback, actions.ToImmutable(), inputAnswer, "Query is an immutable input fact.");

        for (var round = 0; round <= problem.Atoms.Length; round++)
        {
            var changed = false;
            foreach (var rule in problem.Rules)
            {
                charge(checked(1L + rule.Premises.Length));
                var ruleId = HornTask.RuleInputId(rule.Id);
                var available = rule.Premises.Where(bindings.ContainsKey).Select(atom => bindings[atom]).ToArray();
                if (bindings.ContainsKey(rule.Conclusion) || available.Length != rule.Premises.Length)
                {
                    Add(new TaskPlannedAction($"visit:{round}:{rule.Id}", "NO_OP", new[] { ruleId }.Concat(available),
                        argument: $"Fair rule visit round {round}: {(bindings.ContainsKey(rule.Conclusion) ? "conclusion already verified" : "premises not yet available")}."));
                    continue;
                }
                var result = "derived:" + rule.Conclusion;
                Add(new TaskPlannedAction(result, HornTask.ApplyRule, new[] { ruleId }.Concat(available)));
                bindings.Add(rule.Conclusion, result);
                changed = true;
                if (rule.Conclusion == problem.Query)
                    return new(ExecutionMode.FairFallback, actions.ToImmutable(), result, "Finite fair schedule produced the query derivation.");
            }
            if (!changed) break;
        }

        charge(checked(1L + bindings.Count + problem.Rules.Length));
        var candidate = TaskCandidates.HornClosedSet(problem, bindings.Keys);
        Add(new TaskPlannedAction("closed:candidate", HornTask.ProposeClosedSet, [], candidate));
        Add(new TaskPlannedAction("closed:0", HornTask.BeginClosedSetCheck, ["closed:candidate"]));
        var accumulator = "closed:0";
        for (var index = 0; index < problem.Rules.Length; index++)
        {
            var next = "closed:" + (index + 1);
            Add(new TaskPlannedAction(next, HornTask.CheckClosedSetNext,
                [accumulator, "closed:candidate", HornTask.RuleInputId(problem.Rules[index].Id)]));
            accumulator = next;
        }
        Add(new TaskPlannedAction("closed:answer", HornTask.FinalizeClosedSetCheck, [accumulator]));
        return new(ExecutionMode.FairFallback, actions.ToImmutable(), "closed:answer", "Finite fair schedule reached a closed set excluding the query.");
    }
}
