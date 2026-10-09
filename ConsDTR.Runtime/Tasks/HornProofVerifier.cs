namespace ConsDTR.Runtime.Tasks;

/// <summary>Separate implementation of Horn proof and closed-model semantics, without online kernel calls.</summary>
internal sealed class HornProofVerifier : TaskProofVerifier
{
    protected override TaskDefinition ReconstructDefinition(TaskDefinition supplied) =>
        new HornTask(HornProblem.Parse(supplied.CanonicalInput, supplied.CanonicalQuery)).Definition;

    protected override bool VerifyTaskOperation(TaskDefinition input, ProofEvent proof, IReadOnlyList<CheckedProof> parents, out string reason)
    {
        var problem = HornProblem.Parse(input.CanonicalInput, input.CanonicalQuery);
        var ruleIds = problem.Rules.Select(rule => HornTask.RuleInputId(rule.Id)).Order(StringComparer.Ordinal).ToArray();
        reason = "Horn operation has invalid transported semantic premises or output.";
        if (proof.Operation == HornTask.ApplyRule)
        {
            var ruleParent = One(parents, "horn.rule");
            var rule = problem.Rules.SingleOrDefault(candidate => HornTask.RuleInputId(candidate.Id) == ruleParent.RootInputId);
            if (rule is null || ruleParent.Value.Data != TaskJson.Write(rule.ToDocument()) || proof.RuleOrConstraintInputId != ruleParent.RootInputId ||
                parents.Count != rule.Premises.Length + 1 || proof.ExactOutput != HornTask.Atom(rule.Conclusion))
                return false;
            var atomParents = parents.Where(parent => parent.Value.Type == "horn.atom" && parent.Value.Trust == ValueTrust.Verified)
                .Select(parent => TaskJson.Read<string>(parent.Value.Data)).ToHashSet(StringComparer.Ordinal);
            return atomParents.Count == rule.Premises.Length && rule.Premises.All(atomParents.Contains);
        }
        if (proof.Operation == HornTask.ProposeClosedSet)
            return parents.Count == 0 && proof.ProposedCandidate == proof.ExactOutput && ValidSet(problem, proof.ExactOutput, out _, false);
        if (proof.Operation == HornTask.ExtendClosedSet)
        {
            if (parents.Count != 1 || proof.ProposedCandidate is not { } proposed || proof.RuleOrConstraintInputId is not null) return false;
            var previous = One(parents, "horn.set", false).Value;
            if (!ValidSet(problem, previous, out var before, false) ||
                !ValidSet(problem, proposed, out var delta, false, "horn.set-delta") || delta.Revision != checked(before.Revision + 1)) return false;
            var members = before.Members.Union(delta.Members, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            return ExactOutput(proof, "horn.set", new TaskCandidates.SetValue(problem.Fingerprint, delta.Revision, members), ValueTrust.Candidate);
        }
        if (proof.Operation == HornTask.BeginClosedSetCheck)
        {
            if (parents.Count != 1) return false;
            var candidate = One(parents, "horn.set", false).Value;
            if (!ValidSet(problem, candidate, out var set)) return false;
            return ExactOutput(proof, "horn.closed-check", new TaskCheckAccumulator("horn", problem.Fingerprint, set.Revision, candidate, ruleIds, []));
        }
        if (proof.Operation == HornTask.CheckClosedSetNext)
        {
            if (parents.Count != 3) return false;
            var before = TaskJson.Read<TaskCheckAccumulator>(One(parents, "horn.closed-check").Value.Data);
            var candidate = One(parents, "horn.set", false).Value;
            var ruleParent = One(parents, "horn.rule");
            var rule = problem.Rules.SingleOrDefault(value => HornTask.RuleInputId(value.Id) == ruleParent.RootInputId);
            if (!ValidSet(problem, candidate, out var set) || !ValidAccumulator(before, problem.Fingerprint, ruleIds, set.Revision, candidate) ||
                rule is null || ruleParent.Value.Data != TaskJson.Write(rule.ToDocument()) || proof.RuleOrConstraintInputId != ruleParent.RootInputId ||
                before.CheckedInputIds.Contains(ruleParent.RootInputId!, StringComparer.Ordinal))
                return false;
            var model = set.Members.ToHashSet(StringComparer.Ordinal);
            if (rule.Premises.All(model.Contains) && !model.Contains(rule.Conclusion)) return false;
            var after = before with { CheckedInputIds = before.CheckedInputIds.Append(ruleParent.RootInputId!).Order(StringComparer.Ordinal).ToArray() };
            return ExactOutput(proof, "horn.closed-check", after);
        }
        if (proof.Operation == HornTask.FinalizeClosedSetCheck)
        {
            if (parents.Count != 1) return false;
            var state = TaskJson.Read<TaskCheckAccumulator>(One(parents, "horn.closed-check").Value.Data);
            return ValidSet(problem, state.Candidate, out var set) && ValidAccumulator(state, problem.Fingerprint, ruleIds, set.Revision, state.Candidate) &&
                state.CheckedInputIds.SequenceEqual(ruleIds, StringComparer.Ordinal) && ExactOutput(proof, "horn.not-derivable", state);
        }
        reason = "Unsupported Horn semantic operation.";
        return false;
    }

    protected override SemanticVerification VerifyOutput(TaskDefinition input, ExactValue output)
    {
        var problem = HornProblem.Parse(input.CanonicalInput, input.CanonicalQuery);
        if (output == HornTask.Atom(problem.Query)) return new(true, "DERIVABLE: exact Horn derivation independently verified.");
        if (output.Type != "horn.not-derivable") return new(false, "The complete output predicate is neither the query derivation nor NOT_DERIVABLE.");
        var state = TaskJson.Read<TaskCheckAccumulator>(output.Data);
        var ruleIds = problem.Rules.Select(rule => HornTask.RuleInputId(rule.Id)).Order(StringComparer.Ordinal).ToArray();
        if (!ValidSet(problem, state.Candidate, out var candidate) || !ValidAccumulator(state, problem.Fingerprint, ruleIds, candidate.Revision, state.Candidate) ||
            !state.CheckedInputIds.SequenceEqual(ruleIds, StringComparer.Ordinal)) return new(false, "Incomplete or unbound closed-set certificate.");
        // Full independent final certificate check also covers every input clause, rather than trusting its claimed coverage.
        var model = candidate.Members.ToHashSet(StringComparer.Ordinal);
        foreach (var rule in problem.Rules)
            if (rule.Premises.All(model.Contains) && !model.Contains(rule.Conclusion))
                return new(false, "Negative model is not closed under every input rule.");
        return new(true, "NOT_DERIVABLE: complete closed-model certificate independently verified.", true);
    }

    private static bool ValidSet(HornProblem problem, ExactValue value, out TaskCandidates.SetValue set, bool complete = true, string type = "horn.set")
    {
        set = TaskJson.Read<TaskCandidates.SetValue>(value.Data);
        var members = set.Members.ToHashSet(StringComparer.Ordinal);
        return value.Type == type && value.Trust == ValueTrust.Candidate && set.Fingerprint == problem.Fingerprint && set.Revision > 0 &&
            members.Count == set.Members.Length && set.Members.SequenceEqual(set.Members.Order(StringComparer.Ordinal), StringComparer.Ordinal) &&
            members.All(problem.Atoms.Contains) && (!complete || (problem.Facts.All(members.Contains) && !members.Contains(problem.Query)));
    }

    private static bool ValidAccumulator(TaskCheckAccumulator state, string fingerprint, string[] ruleIds, long revision, ExactValue candidate) =>
        state.Kind == "horn" && state.Fingerprint == fingerprint && state.CandidateRevision == revision && state.Candidate == candidate &&
        state.ExpectedInputIds.SequenceEqual(ruleIds, StringComparer.Ordinal) &&
        state.CheckedInputIds.SequenceEqual(state.CheckedInputIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), StringComparer.Ordinal) &&
        state.CheckedInputIds.All(id => ruleIds.Contains(id, StringComparer.Ordinal));
}
