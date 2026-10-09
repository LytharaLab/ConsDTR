namespace ConsDTR.Runtime.Tasks;

public sealed class ReachabilityTask : IKernelTask
{
    public const string Semantics = "Reachability/v1";
    public const string StartPath = "START_PATH";
    public const string ExtendPath = "EXTEND_PATH";
    public const string ProposeCut = "PROPOSE_CUT";
    public const string ExtendCut = "EXTEND_CUT";
    public const string BeginCutCheck = "BEGIN_CUT_CHECK";
    public const string CheckCutNext = "CHECK_CUT_NEXT";
    public const string FinalizeCutCheck = "FINALIZE_CUT_CHECK";
    public GraphPathProblem Problem { get; }
    public TaskDefinition Definition { get; }
    private readonly Dictionary<string, DirectedEdge> _edges;

    public ReachabilityTask(GraphPathProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        Problem = problem;
        _edges = problem.Edges.ToDictionary(edge => EdgeInputId(edge.Id), StringComparer.Ordinal);
        var inputs = new[] { new TaskInput("query", new ExactValue("path.query", problem.CanonicalQuery)) }
            .Concat(problem.Vertices.Select(vertex => new TaskInput(VertexInputId(vertex), new ExactValue("path.vertex", TaskJson.Write(vertex)))))
            .Concat(problem.Edges.Select(edge => new TaskInput(EdgeInputId(edge.Id), new ExactValue("path.edge", TaskJson.Write(edge)))))
            .ToArray();
        Definition = new TaskDefinition("Reachability", Semantics, problem.CanonicalInput, problem.CanonicalQuery, Array.AsReadOnly(inputs));
    }

    public static string EdgeInputId(string edge) => "edge:" + edge;
    public static string VertexInputId(string vertex) => "vertex:" + vertex;
    public ITaskVerifier CreateIndependentVerifier() => new ReachabilityProofVerifier();

    public KernelCheck Check(KernelAction action, KernelReadView view)
    {
        try
        {
            return action.Operation switch
            {
                StartPath => Start(view),
                ExtendPath => Extend(view),
                ProposeCut => Propose(action, view),
                ExtendCut => ExtendCutCandidate(action, view),
                BeginCutCheck => Begin(view),
                CheckCutNext => Next(view),
                FinalizeCutCheck => Finalize(view),
                _ => KernelCheck.Reject("Unsupported reachability operation.")
            };
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException or NullReferenceException or IndexOutOfRangeException or OverflowException)
        { return KernelCheck.Reject("Invalid reachability proposal: " + error.Message); }
    }

    private KernelCheck Start(KernelReadView view)
    {
        TaskOperands.Count(view, 1);
        var query = TaskOperands.One(view, "path.query");
        if (query.InputId != "query" || query.Value.Data != Problem.CanonicalQuery)
            return KernelCheck.Reject("The query must arrive as the exact bound input query token.");
        return KernelCheck.Accept(new ExactValue("path.walk", TaskJson.Write(new PathValue(Problem.Fingerprint, [Problem.Source], []))), "query");
    }

    private KernelCheck Extend(KernelReadView view)
    {
        TaskOperands.Count(view, 2);
        var walk = TaskJson.Read<PathValue>(TaskOperands.One(view, "path.walk").Value.Data);
        var token = TaskOperands.One(view, "path.edge");
        if (!_edges.TryGetValue(token.InputId, out var edge) || token.Value.Data != TaskJson.Write(edge) ||
            walk.Fingerprint != Problem.Fingerprint || walk.Vertices.Length == 0 || walk.EdgeIds.Length + 1 != walk.Vertices.Length ||
            walk.Vertices[^1] != edge.Source || (Problem.RequireSimplePath && walk.Vertices.Contains(edge.Target, StringComparer.Ordinal)))
            return KernelCheck.Reject("The transported edge does not extend the current verified path under this task's semantics.");
        return KernelCheck.Accept(new ExactValue("path.walk", TaskJson.Write(walk with
        { Vertices = walk.Vertices.Append(edge.Target).ToArray(), EdgeIds = walk.EdgeIds.Append(edge.Id).ToArray() })), token.InputId);
    }

    private KernelCheck Propose(KernelAction action, KernelReadView view)
    {
        TaskOperands.Count(view, 0);
        TaskOperands.RequireCandidate(action.Candidate, "path.cut");
        ValidateCut(action.Candidate!, false);
        return KernelCheck.Accept(action.Candidate!);
    }

    private KernelCheck ExtendCutCandidate(KernelAction action, KernelReadView view)
    {
        TaskOperands.Count(view, 1);
        var before = ValidateCut(TaskOperands.One(view, "path.cut", false).Value, false);
        TaskOperands.RequireCandidate(action.Candidate, "path.cut-delta");
        var delta = ValidateCut(action.Candidate!, false, "path.cut-delta");
        var nextRevision = checked(before.Revision + 1);
        if (delta.Revision != nextRevision)
            return KernelCheck.Reject("A cut delta must target exactly the next immutable candidate revision.");
        return KernelCheck.Accept(TaskCandidates.GraphCut(Problem, before.Members.Concat(delta.Members), nextRevision));
    }

    private KernelCheck Begin(KernelReadView view)
    {
        TaskOperands.Count(view, 1);
        var candidate = TaskOperands.One(view, "path.cut", false).Value;
        var cut = ValidateCut(candidate);
        return KernelCheck.Accept(new ExactValue("path.cut-check", TaskJson.Write(
            TaskCheckAccumulator.Begin("cut", Problem.Fingerprint, cut.Revision, candidate, _edges.Keys))));
    }

    private KernelCheck Next(KernelReadView view)
    {
        TaskOperands.Count(view, 3);
        var state = TaskJson.Read<TaskCheckAccumulator>(TaskOperands.One(view, "path.cut-check").Value.Data);
        var candidate = TaskOperands.One(view, "path.cut", false).Value;
        var cut = ValidateCut(candidate);
        var token = TaskOperands.One(view, "path.edge");
        if (!state.Matches("cut", Problem.Fingerprint, _edges.Keys, candidate) || state.CandidateRevision != cut.Revision ||
            !_edges.TryGetValue(token.InputId, out var edge) || token.Value.Data != TaskJson.Write(edge))
            return KernelCheck.Reject("Cut accumulator, revision or edge input binding is invalid.");
        if (cut.Members.Contains(edge.Source, StringComparer.Ordinal) && !cut.Members.Contains(edge.Target, StringComparer.Ordinal))
            return KernelCheck.Reject("An input edge leaves the proposed closed cut.");
        return KernelCheck.Accept(new ExactValue("path.cut-check", TaskJson.Write(state.Next(token.InputId))), token.InputId);
    }

    private KernelCheck Finalize(KernelReadView view)
    {
        TaskOperands.Count(view, 1);
        var state = TaskJson.Read<TaskCheckAccumulator>(TaskOperands.One(view, "path.cut-check").Value.Data);
        var cut = ValidateCut(state.Candidate);
        if (!state.Matches("cut", Problem.Fingerprint, _edges.Keys) || state.CandidateRevision != cut.Revision || !state.Complete)
            return KernelCheck.Reject("All input edges must be checked under the same immutable cut revision.");
        return KernelCheck.Accept(new ExactValue("path.unreachable", TaskJson.Write(state)));
    }

    private TaskCandidates.SetValue ValidateCut(ExactValue candidate, bool complete = true, string type = "path.cut")
    {
        TaskOperands.RequireCandidate(candidate, type);
        var cut = TaskJson.Read<TaskCandidates.SetValue>(candidate.Data);
        if (cut.Fingerprint != Problem.Fingerprint || cut.Revision < 1 ||
            !cut.Members.SequenceEqual(TaskIdentity.Set(cut.Members, "members"), StringComparer.Ordinal) ||
            cut.Members.Any(vertex => !Problem.Vertices.Contains(vertex)) ||
            (complete && (!cut.Members.Contains(Problem.Source, StringComparer.Ordinal) || cut.Members.Contains(Problem.Target, StringComparer.Ordinal))))
            throw new ArgumentException("Candidate members must be a declared-vertex subset; a checked cut must contain source and exclude target.");
        return cut;
    }
}
