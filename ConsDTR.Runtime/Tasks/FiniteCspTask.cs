namespace ConsDTR.Runtime.Tasks;

public sealed class FiniteCspTask : IKernelTask
{
    public const string Semantics = "FiniteCSP/v1";
    public const string ProposeAssignment = "PROPOSE_ASSIGNMENT";
    public const string ExtendAssignment = "EXTEND_ASSIGNMENT";
    public const string CheckConstraint = "CHECK_CONSTRAINT";
    public const string BeginAssignmentCheck = "BEGIN_ASSIGNMENT_CHECK";
    public const string CheckAssignmentNext = "CHECK_ASSIGNMENT_NEXT";
    public const string FinalizeAssignmentCheck = "FINALIZE_ASSIGNMENT_CHECK";
    public CspProblem Problem { get; }
    public TaskDefinition Definition { get; }
    private readonly Dictionary<string, FiniteConstraint> _constraints;

    public FiniteCspTask(CspProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        Problem = problem;
        _constraints = problem.Constraints.ToDictionary(constraint => ConstraintInputId(constraint.Id), StringComparer.Ordinal);
        var inputs = new[] { new TaskInput("schema", new ExactValue("csp.schema", TaskJson.Write(problem.Variables.Select(variable => variable.ToDocument()).ToArray()))) }
            .Concat(problem.Variables.Select(variable => new TaskInput(VariableInputId(variable.Id), new ExactValue("csp.variable", TaskJson.Write(variable.ToDocument())))))
            .Concat(problem.Constraints.Select(constraint => new TaskInput(ConstraintInputId(constraint.Id), new ExactValue("csp.constraint", TaskJson.Write(constraint.ToDocument())))))
            .ToArray();
        Definition = new TaskDefinition("FiniteCSP", Semantics, problem.CanonicalInput, problem.CanonicalQuery, Array.AsReadOnly(inputs));
    }

    public static string ConstraintInputId(string id) => "constraint:" + id;
    public static string VariableInputId(string id) => "variable:" + id;
    public ITaskVerifier CreateIndependentVerifier() => new CspProofVerifier();

    public KernelCheck Check(KernelAction action, KernelReadView view)
    {
        try
        {
            return action.Operation switch
            {
                ProposeAssignment => Propose(action, view),
                ExtendAssignment => Extend(action, view),
                CheckConstraint => CheckOne(view),
                BeginAssignmentCheck => Begin(view),
                CheckAssignmentNext => Next(view),
                FinalizeAssignmentCheck => Finalize(view),
                _ => KernelCheck.Reject("Unsupported finite CSP operation.")
            };
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException or NullReferenceException or IndexOutOfRangeException or OverflowException)
        { return KernelCheck.Reject("Invalid finite CSP proposal: " + error.Message); }
    }

    private KernelCheck Propose(KernelAction action, KernelReadView view)
    {
        TaskOperands.Count(view, 0);
        TaskOperands.RequireCandidate(action.Candidate, "csp.assignment");
        ValidateAssignment(action.Candidate!, false);
        return KernelCheck.Accept(action.Candidate!);
    }

    private KernelCheck Extend(KernelAction action, KernelReadView view)
    {
        TaskOperands.Count(view, 1);
        var before = ValidateAssignment(TaskOperands.One(view, "csp.assignment", false).Value, false);
        TaskOperands.RequireCandidate(action.Candidate, "csp.assignment-delta");
        var delta = ValidateAssignment(action.Candidate!, false, "csp.assignment-delta");
        var nextRevision = checked(before.Revision + 1);
        if (delta.Revision != nextRevision)
            return KernelCheck.Reject("An assignment delta must target exactly the next immutable candidate revision.");
        var values = before.Entries.ToDictionary(entry => entry.Variable, entry => entry.Value, StringComparer.Ordinal);
        foreach (var entry in delta.Entries) values[entry.Variable] = entry.Value;
        return KernelCheck.Accept(TaskCandidates.Assignment(Problem, values, nextRevision));
    }

    private KernelCheck CheckOne(KernelReadView view)
    {
        TaskOperands.Count(view, 2);
        var candidate = TaskOperands.One(view, "csp.assignment", false).Value;
        var assignment = ValidateAssignment(candidate, true);
        var token = TaskOperands.One(view, "csp.constraint");
        if (!_constraints.TryGetValue(token.InputId, out var constraint) || token.Value.Data != TaskJson.Write(constraint.ToDocument()) ||
            !Evaluate(constraint, assignment.Entries.ToDictionary(entry => entry.Variable, entry => entry.Value, StringComparer.Ordinal)))
            return KernelCheck.Reject("This constraint is not satisfied by the transported assignment revision.");
        return KernelCheck.Accept(new ExactValue("csp.constraint-check", TaskJson.Write(new ConstraintCheckValue(
            Problem.Fingerprint, assignment.Revision, candidate, token.InputId))), token.InputId);
    }

    private KernelCheck Begin(KernelReadView view)
    {
        TaskOperands.Count(view, 1);
        var candidate = TaskOperands.One(view, "csp.assignment", false).Value;
        var assignment = ValidateAssignment(candidate, true);
        return KernelCheck.Accept(new ExactValue("csp.assignment-check", TaskJson.Write(
            TaskCheckAccumulator.Begin("assignment", Problem.Fingerprint, assignment.Revision, candidate, _constraints.Keys))));
    }

    private KernelCheck Next(KernelReadView view)
    {
        TaskOperands.Count(view, 3);
        var state = TaskJson.Read<TaskCheckAccumulator>(TaskOperands.One(view, "csp.assignment-check").Value.Data);
        var candidate = TaskOperands.One(view, "csp.assignment", false).Value;
        var assignment = ValidateAssignment(candidate, true);
        var token = TaskOperands.One(view, "csp.constraint");
        if (!state.Matches("assignment", Problem.Fingerprint, _constraints.Keys, candidate) || state.CandidateRevision != assignment.Revision ||
            !_constraints.TryGetValue(token.InputId, out var constraint) || token.Value.Data != TaskJson.Write(constraint.ToDocument()) ||
            !Evaluate(constraint, assignment.Entries.ToDictionary(entry => entry.Variable, entry => entry.Value, StringComparer.Ordinal)))
            return KernelCheck.Reject("Accumulator, assignment revision or this constraint check is invalid.");
        return KernelCheck.Accept(new ExactValue("csp.assignment-check", TaskJson.Write(state.Next(token.InputId))), token.InputId);
    }

    private KernelCheck Finalize(KernelReadView view)
    {
        TaskOperands.Count(view, 1);
        var state = TaskJson.Read<TaskCheckAccumulator>(TaskOperands.One(view, "csp.assignment-check").Value.Data);
        var assignment = ValidateAssignment(state.Candidate, true);
        if (!state.Matches("assignment", Problem.Fingerprint, _constraints.Keys) || state.CandidateRevision != assignment.Revision || !state.Complete)
            return KernelCheck.Reject("All constraints must be covered by the same immutable complete assignment.");
        return KernelCheck.Accept(new ExactValue("csp.satisfies", TaskJson.Write(state)));
    }

    private TaskCandidates.AssignmentValue ValidateAssignment(ExactValue candidate, bool complete, string type = "csp.assignment")
    {
        TaskOperands.RequireCandidate(candidate, type);
        var assignment = TaskJson.Read<TaskCandidates.AssignmentValue>(candidate.Data);
        var variables = Problem.Variables.ToDictionary(variable => variable.Id, StringComparer.Ordinal);
        if (assignment.Fingerprint != Problem.Fingerprint || assignment.Revision < 1 ||
            assignment.Entries.Select(entry => entry.Variable).Distinct(StringComparer.Ordinal).Count() != assignment.Entries.Length ||
            !assignment.Entries.Select(entry => entry.Variable).SequenceEqual(assignment.Entries.Select(entry => entry.Variable).Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            assignment.Entries.Any(entry => !variables.TryGetValue(entry.Variable, out var variable) || !variable.Domain.Contains(entry.Value)) ||
            (complete && assignment.Entries.Length != variables.Count))
            throw new ArgumentException("Assignments require unique declared variables, legal finite-domain values and a positive bound revision.");
        return assignment;
    }

    private static bool Evaluate(FiniteConstraint constraint, IReadOnlyDictionary<string, int> assignment) => constraint.Kind switch
    {
        FiniteConstraint.ConstraintKind.Equal => assignment[constraint.Variables[0]] == assignment[constraint.Variables[1]],
        FiniteConstraint.ConstraintKind.NotEqual => assignment[constraint.Variables[0]] != assignment[constraint.Variables[1]],
        FiniteConstraint.ConstraintKind.BooleanClause => constraint.Literals.Any(literal => (assignment[literal.Variable] == 1) != literal.Negated),
        FiniteConstraint.ConstraintKind.AllowedTable => constraint.AllowedTuples.Any(tuple =>
            tuple.Select((value, index) => assignment[constraint.Variables[index]] == value).All(matches => matches)),
        _ => false
    };

    internal sealed record ConstraintCheckValue(string Fingerprint, long AssignmentRevision, ExactValue Candidate, string ConstraintInputId);
}
