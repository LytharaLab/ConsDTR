namespace ConsDTR.Runtime.Tasks;

public sealed class HornTask : IKernelTask
{
    public const string Semantics = "GroundHorn/v1";
    public const string ApplyRule = "APPLY_GROUND_RULE";
    public const string ProposeClosedSet = "PROPOSE_CLOSED_SET";
    public const string ExtendClosedSet = "EXTEND_CLOSED_SET";
    public const string BeginClosedSetCheck = "BEGIN_CLOSED_SET_CHECK";
    public const string CheckClosedSetNext = "CHECK_CLOSED_SET_NEXT";
    public const string FinalizeClosedSetCheck = "FINALIZE_CLOSED_SET_CHECK";
    public HornProblem Problem { get; }
    public TaskDefinition Definition { get; }
    private readonly Dictionary<string, HornRule> _rules;

    public HornTask(HornProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        Problem = problem;
        _rules = problem.Rules.ToDictionary(rule => RuleInputId(rule.Id), StringComparer.Ordinal);
        var inputs = problem.Atoms.Select(atom => new TaskInput(SymbolInputId(atom), new ExactValue("horn.symbol", TaskJson.Write(atom))))
            .Concat(problem.Facts.Select(atom => new TaskInput(FactInputId(atom), Atom(atom))))
            .Concat(problem.Rules.Select(rule => new TaskInput(RuleInputId(rule.Id), new ExactValue("horn.rule", TaskJson.Write(rule.ToDocument())))))
            .ToArray();
        Definition = new TaskDefinition("GroundHorn", Semantics, problem.CanonicalInput, problem.CanonicalQuery, Array.AsReadOnly(inputs));
    }

    public static string FactInputId(string atom) => "fact:" + atom;
    public static string SymbolInputId(string atom) => "atom:" + atom;
    public static string RuleInputId(string rule) => "rule:" + rule;
    public static ExactValue Atom(string atom) => new("horn.atom", TaskJson.Write(TaskIdentity.Require(atom, nameof(atom))));
    public ITaskVerifier CreateIndependentVerifier() => new HornProofVerifier();

    public KernelCheck Check(KernelAction action, KernelReadView view)
    {
        try
        {
            return action.Operation switch
            {
                ApplyRule => Apply(view),
                ProposeClosedSet => Propose(action, view),
                ExtendClosedSet => Extend(action, view),
                BeginClosedSetCheck => Begin(view),
                CheckClosedSetNext => Next(view),
                FinalizeClosedSetCheck => Finalize(view),
                _ => KernelCheck.Reject("Unsupported Horn operation.")
            };
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException or NullReferenceException or IndexOutOfRangeException or OverflowException)
        { return KernelCheck.Reject("Invalid Horn proposal: " + error.Message); }
    }

    private KernelCheck Apply(KernelReadView view)
    {
        var token = TaskOperands.One(view, "horn.rule");
        if (!_rules.TryGetValue(token.InputId, out var rule) || token.Value.Data != TaskJson.Write(rule.ToDocument()))
            return KernelCheck.Reject("Rule token is not a bound input rule.");
        TaskOperands.Count(view, rule.Premises.Length + 1);
        var atoms = view.Operands.Where(operand => operand.Value.Type == "horn.atom" && operand.Value.Trust == ValueTrust.Verified)
            .Select(operand => TaskJson.Read<string>(operand.Value.Data)).Order(StringComparer.Ordinal).ToArray();
        if (!atoms.SequenceEqual(rule.Premises, StringComparer.Ordinal))
            return KernelCheck.Reject("Current transported atom tokens do not exactly match the normalized rule premises.");
        return KernelCheck.Accept(Atom(rule.Conclusion), token.InputId);
    }

    private KernelCheck Propose(KernelAction action, KernelReadView view)
    {
        TaskOperands.Count(view, 0);
        TaskOperands.RequireCandidate(action.Candidate, "horn.set");
        ValidateSet(action.Candidate!, false);
        return KernelCheck.Accept(action.Candidate!);
    }

    private KernelCheck Extend(KernelAction action, KernelReadView view)
    {
        TaskOperands.Count(view, 1);
        var before = ValidateSet(TaskOperands.One(view, "horn.set", false).Value, false);
        TaskOperands.RequireCandidate(action.Candidate, "horn.set-delta");
        var delta = ValidateSet(action.Candidate!, false, "horn.set-delta");
        var nextRevision = checked(before.Revision + 1);
        if (delta.Revision != nextRevision)
            return KernelCheck.Reject("A closed-set delta must target exactly the next immutable candidate revision.");
        return KernelCheck.Accept(TaskCandidates.HornClosedSet(Problem, before.Members.Concat(delta.Members), nextRevision));
    }

    private KernelCheck Begin(KernelReadView view)
    {
        TaskOperands.Count(view, 1);
        var candidate = TaskOperands.One(view, "horn.set", false).Value;
        var set = ValidateSet(candidate);
        var state = TaskCheckAccumulator.Begin("horn", Problem.Fingerprint, set.Revision, candidate, _rules.Keys);
        return KernelCheck.Accept(new ExactValue("horn.closed-check", TaskJson.Write(state)));
    }

    private KernelCheck Next(KernelReadView view)
    {
        TaskOperands.Count(view, 3);
        var state = TaskJson.Read<TaskCheckAccumulator>(TaskOperands.One(view, "horn.closed-check").Value.Data);
        var candidate = TaskOperands.One(view, "horn.set", false).Value;
        var set = ValidateSet(candidate);
        var token = TaskOperands.One(view, "horn.rule");
        if (!state.Matches("horn", Problem.Fingerprint, _rules.Keys, candidate) || state.CandidateRevision != set.Revision ||
            !_rules.TryGetValue(token.InputId, out var rule) || token.Value.Data != TaskJson.Write(rule.ToDocument()))
            return KernelCheck.Reject("Check accumulator, candidate revision or rule binding is invalid.");
        var members = set.Members.ToHashSet(StringComparer.Ordinal);
        if (rule.Premises.All(members.Contains) && !members.Contains(rule.Conclusion))
            return KernelCheck.Reject("The proposed set is not closed under this rule.");
        return KernelCheck.Accept(new ExactValue("horn.closed-check", TaskJson.Write(state.Next(token.InputId))), token.InputId);
    }

    private KernelCheck Finalize(KernelReadView view)
    {
        TaskOperands.Count(view, 1);
        var state = TaskJson.Read<TaskCheckAccumulator>(TaskOperands.One(view, "horn.closed-check").Value.Data);
        var set = ValidateSet(state.Candidate);
        if (!state.Matches("horn", Problem.Fingerprint, _rules.Keys) || state.CandidateRevision != set.Revision || !state.Complete)
            return KernelCheck.Reject("Every input rule must be checked exactly once for the same candidate revision.");
        return KernelCheck.Accept(new ExactValue("horn.not-derivable", TaskJson.Write(state)));
    }

    private TaskCandidates.SetValue ValidateSet(ExactValue candidate, bool complete = true, string type = "horn.set")
    {
        TaskOperands.RequireCandidate(candidate, type);
        var set = TaskJson.Read<TaskCandidates.SetValue>(candidate.Data);
        if (set.Fingerprint != Problem.Fingerprint || set.Revision < 1 ||
            !set.Members.SequenceEqual(TaskIdentity.Set(set.Members, "members"), StringComparer.Ordinal) ||
            set.Members.Any(member => !Problem.Atoms.Contains(member)) ||
            (complete && (Problem.Facts.Any(fact => !set.Members.Contains(fact, StringComparer.Ordinal)) || set.Members.Contains(Problem.Query, StringComparer.Ordinal))))
            throw new ArgumentException("Candidate members must be a finite universe subset; a checked negative candidate must include every fact and exclude the query.");
        return set;
    }
}
