namespace ConsDTR.Runtime.Tasks;

internal sealed class ReachabilityProofVerifier : TaskProofVerifier
{
    protected override TaskDefinition ReconstructDefinition(TaskDefinition supplied) =>
        new ReachabilityTask(GraphPathProblem.Parse(supplied.CanonicalInput, supplied.CanonicalQuery)).Definition;

    protected override bool VerifyTaskOperation(TaskDefinition input, ProofEvent proof, IReadOnlyList<CheckedProof> parents, out string reason)
    {
        var problem = GraphPathProblem.Parse(input.CanonicalInput, input.CanonicalQuery);
        var edgeIds = problem.Edges.Select(edge => ReachabilityTask.EdgeInputId(edge.Id)).Order(StringComparer.Ordinal).ToArray();
        reason = "Reachability operation has invalid semantic premises or output.";
        if (proof.Operation == ReachabilityTask.StartPath)
        {
            if (parents.Count != 1) return false;
            var query = One(parents, "path.query");
            return query.RootInputId == "query" && query.Value.Data == problem.CanonicalQuery && proof.RuleOrConstraintInputId == "query" &&
                ExactOutput(proof, "path.walk", new PathValue(problem.Fingerprint, [problem.Source], []));
        }
        if (proof.Operation == ReachabilityTask.ExtendPath)
        {
            if (parents.Count != 2) return false;
            var before = TaskJson.Read<PathValue>(One(parents, "path.walk").Value.Data);
            var edgeParent = One(parents, "path.edge");
            var edge = problem.Edges.SingleOrDefault(value => ReachabilityTask.EdgeInputId(value.Id) == edgeParent.RootInputId);
            if (edge is null || edgeParent.Value.Data != TaskJson.Write(edge) || proof.RuleOrConstraintInputId != edgeParent.RootInputId ||
                !ValidWalk(problem, before, false) || before.Vertices[^1] != edge.Source ||
                (problem.RequireSimplePath && before.Vertices.Contains(edge.Target, StringComparer.Ordinal))) return false;
            return ExactOutput(proof, "path.walk", new PathValue(problem.Fingerprint,
                before.Vertices.Append(edge.Target).ToArray(), before.EdgeIds.Append(edge.Id).ToArray()));
        }
        if (proof.Operation == ReachabilityTask.ProposeCut)
            return parents.Count == 0 && proof.ProposedCandidate == proof.ExactOutput && ValidCut(problem, proof.ExactOutput, out _, false);
        if (proof.Operation == ReachabilityTask.ExtendCut)
        {
            if (parents.Count != 1 || proof.ProposedCandidate is not { } proposed || proof.RuleOrConstraintInputId is not null) return false;
            var previous = One(parents, "path.cut", false).Value;
            if (!ValidCut(problem, previous, out var before, false) ||
                !ValidCut(problem, proposed, out var delta, false, "path.cut-delta") || delta.Revision != checked(before.Revision + 1)) return false;
            var members = before.Members.Union(delta.Members, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            return ExactOutput(proof, "path.cut", new TaskCandidates.SetValue(problem.Fingerprint, delta.Revision, members), ValueTrust.Candidate);
        }
        if (proof.Operation == ReachabilityTask.BeginCutCheck)
        {
            if (parents.Count != 1) return false;
            var candidate = One(parents, "path.cut", false).Value;
            if (!ValidCut(problem, candidate, out var cut)) return false;
            return ExactOutput(proof, "path.cut-check", new TaskCheckAccumulator("cut", problem.Fingerprint, cut.Revision, candidate, edgeIds, []));
        }
        if (proof.Operation == ReachabilityTask.CheckCutNext)
        {
            if (parents.Count != 3) return false;
            var state = TaskJson.Read<TaskCheckAccumulator>(One(parents, "path.cut-check").Value.Data);
            var candidate = One(parents, "path.cut", false).Value;
            var edgeParent = One(parents, "path.edge");
            var edge = problem.Edges.SingleOrDefault(value => ReachabilityTask.EdgeInputId(value.Id) == edgeParent.RootInputId);
            if (!ValidCut(problem, candidate, out var cut) || !ValidAccumulator(state, problem.Fingerprint, edgeIds, cut.Revision, candidate) ||
                edge is null || edgeParent.Value.Data != TaskJson.Write(edge) || proof.RuleOrConstraintInputId != edgeParent.RootInputId ||
                state.CheckedInputIds.Contains(edgeParent.RootInputId!, StringComparer.Ordinal)) return false;
            var membership = cut.Members.ToHashSet(StringComparer.Ordinal);
            if (membership.Contains(edge.Source) && !membership.Contains(edge.Target)) return false;
            var after = state with { CheckedInputIds = state.CheckedInputIds.Append(edgeParent.RootInputId!).Order(StringComparer.Ordinal).ToArray() };
            return ExactOutput(proof, "path.cut-check", after);
        }
        if (proof.Operation == ReachabilityTask.FinalizeCutCheck)
        {
            if (parents.Count != 1) return false;
            var state = TaskJson.Read<TaskCheckAccumulator>(One(parents, "path.cut-check").Value.Data);
            return ValidCut(problem, state.Candidate, out var cut) && ValidAccumulator(state, problem.Fingerprint, edgeIds, cut.Revision, state.Candidate) &&
                state.CheckedInputIds.SequenceEqual(edgeIds, StringComparer.Ordinal) && ExactOutput(proof, "path.unreachable", state);
        }
        reason = "Unsupported reachability semantic operation.";
        return false;
    }

    protected override SemanticVerification VerifyOutput(TaskDefinition input, ExactValue output)
    {
        var problem = GraphPathProblem.Parse(input.CanonicalInput, input.CanonicalQuery);
        if (output.Type == "path.walk")
            return ValidWalk(problem, TaskJson.Read<PathValue>(output.Data), true)
                ? new(true, "PATH_EXISTS: every edge and the complete endpoint predicate independently verified; no shortest-path claim.")
                : new(false, "The final walk is invalid or does not reach the exact query target.");
        if (output.Type != "path.unreachable") return new(false, "Unsupported reachability output predicate.");
        var state = TaskJson.Read<TaskCheckAccumulator>(output.Data);
        var edgeIds = problem.Edges.Select(edge => ReachabilityTask.EdgeInputId(edge.Id)).Order(StringComparer.Ordinal).ToArray();
        if (!ValidCut(problem, state.Candidate, out var cut) || !ValidAccumulator(state, problem.Fingerprint, edgeIds, cut.Revision, state.Candidate) ||
            !state.CheckedInputIds.SequenceEqual(edgeIds, StringComparer.Ordinal)) return new(false, "Incomplete or unbound cut certificate.");
        var membership = cut.Members.ToHashSet(StringComparer.Ordinal);
        foreach (var edge in problem.Edges)
            if (membership.Contains(edge.Source) && !membership.Contains(edge.Target))
                return new(false, "An input edge leaves the claimed closed cut.");
        return new(true, "UNREACHABLE: complete closed-cut certificate independently verified.", true);
    }

    private static bool ValidWalk(GraphPathProblem problem, PathValue walk, bool requireTarget)
    {
        if (walk.Fingerprint != problem.Fingerprint || walk.Vertices.Length == 0 || walk.Vertices[0] != problem.Source ||
            walk.EdgeIds.Length != walk.Vertices.Length - 1 || (requireTarget && walk.Vertices[^1] != problem.Target) ||
            (problem.RequireSimplePath && walk.Vertices.Distinct(StringComparer.Ordinal).Count() != walk.Vertices.Length)) return false;
        var edges = problem.Edges.ToDictionary(edge => edge.Id, StringComparer.Ordinal);
        for (var index = 0; index < walk.EdgeIds.Length; index++)
            if (!edges.TryGetValue(walk.EdgeIds[index], out var edge) || edge.Source != walk.Vertices[index] || edge.Target != walk.Vertices[index + 1])
                return false;
        return true;
    }

    private static bool ValidCut(GraphPathProblem problem, ExactValue value, out TaskCandidates.SetValue cut, bool complete = true, string type = "path.cut")
    {
        cut = TaskJson.Read<TaskCandidates.SetValue>(value.Data);
        var membership = cut.Members.ToHashSet(StringComparer.Ordinal);
        return value.Type == type && value.Trust == ValueTrust.Candidate && cut.Fingerprint == problem.Fingerprint && cut.Revision > 0 &&
            membership.Count == cut.Members.Length && cut.Members.SequenceEqual(cut.Members.Order(StringComparer.Ordinal), StringComparer.Ordinal) &&
            membership.All(problem.Vertices.Contains) && (!complete || (membership.Contains(problem.Source) && !membership.Contains(problem.Target)));
    }

    private static bool ValidAccumulator(TaskCheckAccumulator state, string fingerprint, string[] edgeIds, long revision, ExactValue candidate) =>
        state.Kind == "cut" && state.Fingerprint == fingerprint && state.CandidateRevision == revision && state.Candidate == candidate &&
        state.ExpectedInputIds.SequenceEqual(edgeIds, StringComparer.Ordinal) &&
        state.CheckedInputIds.SequenceEqual(state.CheckedInputIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), StringComparer.Ordinal) &&
        state.CheckedInputIds.All(id => edgeIds.Contains(id, StringComparer.Ordinal));
}
