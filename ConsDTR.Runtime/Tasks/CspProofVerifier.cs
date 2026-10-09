namespace ConsDTR.Runtime.Tasks;

internal sealed class CspProofVerifier : TaskProofVerifier
{
    protected override TaskDefinition ReconstructDefinition(TaskDefinition supplied) =>
        new FiniteCspTask(CspProblem.Parse(supplied.CanonicalInput, supplied.CanonicalQuery)).Definition;

    protected override bool VerifyTaskOperation(TaskDefinition input, ProofEvent proof, IReadOnlyList<CheckedProof> parents, out string reason)
    {
        var problem = CspProblem.Parse(input.CanonicalInput, input.CanonicalQuery);
        var ids = problem.Constraints.Select(constraint => FiniteCspTask.ConstraintInputId(constraint.Id)).Order(StringComparer.Ordinal).ToArray();
        reason = "CSP operation has invalid semantic premises, assignment revision or exact output.";
        if (proof.Operation == FiniteCspTask.ProposeAssignment)
            return parents.Count == 0 && proof.ProposedCandidate == proof.ExactOutput && ValidAssignment(problem, proof.ExactOutput, false, out _);
        if (proof.Operation == FiniteCspTask.ExtendAssignment)
        {
            if (parents.Count != 1 || proof.ProposedCandidate is not { } proposed || proof.RuleOrConstraintInputId is not null) return false;
            var previous = One(parents, "csp.assignment", false).Value;
            if (!ValidAssignment(problem, previous, false, out var before) ||
                !ValidAssignment(problem, proposed, false, out var delta, "csp.assignment-delta") || delta.Revision != checked(before.Revision + 1)) return false;
            var replaced = delta.Entries.Select(entry => entry.Variable).ToHashSet(StringComparer.Ordinal);
            var entries = before.Entries.Where(entry => !replaced.Contains(entry.Variable)).Concat(delta.Entries)
                .OrderBy(entry => entry.Variable, StringComparer.Ordinal).ToArray();
            return ExactOutput(proof, "csp.assignment", new TaskCandidates.AssignmentValue(problem.Fingerprint, delta.Revision, entries), ValueTrust.Candidate);
        }
        if (proof.Operation == FiniteCspTask.BeginAssignmentCheck)
        {
            if (parents.Count != 1) return false;
            var candidate = One(parents, "csp.assignment", false).Value;
            if (!ValidAssignment(problem, candidate, true, out var assignment)) return false;
            return ExactOutput(proof, "csp.assignment-check", new TaskCheckAccumulator("assignment", problem.Fingerprint, assignment.Revision, candidate, ids, []));
        }
        if (proof.Operation == FiniteCspTask.CheckConstraint || proof.Operation == FiniteCspTask.CheckAssignmentNext)
        {
            var accumulating = proof.Operation == FiniteCspTask.CheckAssignmentNext;
            if (parents.Count != (accumulating ? 3 : 2)) return false;
            var candidate = One(parents, "csp.assignment", false).Value;
            var constraintParent = One(parents, "csp.constraint");
            var constraint = problem.Constraints.SingleOrDefault(value => FiniteCspTask.ConstraintInputId(value.Id) == constraintParent.RootInputId);
            if (!ValidAssignment(problem, candidate, true, out var assignment) || constraint is null ||
                constraintParent.Value.Data != TaskJson.Write(constraint.ToDocument()) || proof.RuleOrConstraintInputId != constraintParent.RootInputId ||
                !Satisfies(constraint, assignment)) return false;
            if (!accumulating)
                return ExactOutput(proof, "csp.constraint-check", new FiniteCspTask.ConstraintCheckValue(problem.Fingerprint, assignment.Revision, candidate, constraintParent.RootInputId!));
            var state = TaskJson.Read<TaskCheckAccumulator>(One(parents, "csp.assignment-check").Value.Data);
            if (!ValidAccumulator(state, problem.Fingerprint, ids, assignment.Revision, candidate) || state.CheckedInputIds.Contains(constraintParent.RootInputId!, StringComparer.Ordinal))
                return false;
            return ExactOutput(proof, "csp.assignment-check", state with
            { CheckedInputIds = state.CheckedInputIds.Append(constraintParent.RootInputId!).Order(StringComparer.Ordinal).ToArray() });
        }
        if (proof.Operation == FiniteCspTask.FinalizeAssignmentCheck)
        {
            if (parents.Count != 1) return false;
            var state = TaskJson.Read<TaskCheckAccumulator>(One(parents, "csp.assignment-check").Value.Data);
            return ValidAssignment(problem, state.Candidate, true, out var assignment) && ValidAccumulator(state, problem.Fingerprint, ids, assignment.Revision, state.Candidate) &&
                state.CheckedInputIds.SequenceEqual(ids, StringComparer.Ordinal) && ExactOutput(proof, "csp.satisfies", state);
        }
        reason = "Unsupported CSP operation; v1 never certifies UNSAT.";
        return false;
    }

    protected override SemanticVerification VerifyOutput(TaskDefinition input, ExactValue output)
    {
        if (output.Type != "csp.satisfies") return new(false, "v1 accepts only the complete SATISFIES predicate, never UNSAT or a partially checked assignment.");
        var problem = CspProblem.Parse(input.CanonicalInput, input.CanonicalQuery);
        var state = TaskJson.Read<TaskCheckAccumulator>(output.Data);
        var ids = problem.Constraints.Select(constraint => FiniteCspTask.ConstraintInputId(constraint.Id)).Order(StringComparer.Ordinal).ToArray();
        if (!ValidAssignment(problem, state.Candidate, true, out var assignment) || !ValidAccumulator(state, problem.Fingerprint, ids, assignment.Revision, state.Candidate) ||
            !state.CheckedInputIds.SequenceEqual(ids, StringComparer.Ordinal)) return new(false, "Incomplete assignment, stale revision or incomplete constraint coverage.");
        foreach (var constraint in problem.Constraints)
            if (!Satisfies(constraint, assignment)) return new(false, "The output assignment violates an exact input constraint.");
        return new(true, "SATISFIES: complete finite-domain assignment and every constraint independently verified.");
    }

    private static bool ValidAssignment(CspProblem problem, ExactValue value, bool complete, out TaskCandidates.AssignmentValue assignment, string type = "csp.assignment")
    {
        assignment = TaskJson.Read<TaskCandidates.AssignmentValue>(value.Data);
        if (value.Type != type || value.Trust != ValueTrust.Candidate || assignment.Fingerprint != problem.Fingerprint || assignment.Revision < 1)
            return false;
        var variables = problem.Variables.ToDictionary(variable => variable.Id, StringComparer.Ordinal);
        var keys = assignment.Entries.Select(entry => entry.Variable).ToArray();
        if (!keys.SequenceEqual(keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), StringComparer.Ordinal) || (complete && keys.Length != variables.Count))
            return false;
        foreach (var entry in assignment.Entries)
            if (!variables.TryGetValue(entry.Variable, out var variable) || !variable.Domain.Contains(entry.Value)) return false;
        return true;
    }

    // Deliberately independent of FiniteCspTask.Evaluate: direct opcode semantics on the serialized witness.
    private static bool Satisfies(FiniteConstraint constraint, TaskCandidates.AssignmentValue assignment)
    {
        var values = assignment.Entries.ToDictionary(entry => entry.Variable, entry => entry.Value, StringComparer.Ordinal);
        switch (constraint.Kind)
        {
            case FiniteConstraint.ConstraintKind.Equal:
                return values[constraint.Variables[0]] == values[constraint.Variables[1]];
            case FiniteConstraint.ConstraintKind.NotEqual:
                return values[constraint.Variables[0]] != values[constraint.Variables[1]];
            case FiniteConstraint.ConstraintKind.BooleanClause:
                foreach (var literal in constraint.Literals)
                    if ((!literal.Negated && values[literal.Variable] == 1) || (literal.Negated && values[literal.Variable] == 0)) return true;
                return false;
            case FiniteConstraint.ConstraintKind.AllowedTable:
                foreach (var tuple in constraint.AllowedTuples)
                {
                    var match = true;
                    for (var index = 0; index < tuple.Length; index++)
                        if (values[constraint.Variables[index]] != tuple[index]) { match = false; break; }
                    if (match) return true;
                }
                return false;
            default:
                return false;
        }
    }

    private static bool ValidAccumulator(TaskCheckAccumulator state, string fingerprint, string[] ids, long revision, ExactValue candidate) =>
        state.Kind == "assignment" && state.Fingerprint == fingerprint && state.CandidateRevision == revision && state.Candidate == candidate &&
        state.ExpectedInputIds.SequenceEqual(ids, StringComparer.Ordinal) &&
        state.CheckedInputIds.SequenceEqual(state.CheckedInputIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), StringComparer.Ordinal) &&
        state.CheckedInputIds.All(id => ids.Contains(id, StringComparer.Ordinal));
}
