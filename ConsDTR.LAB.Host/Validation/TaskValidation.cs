using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using ConsDTR.Lib.Inference;
using ConsDTR.Runtime;
using ConsDTR.Runtime.Tasks;

namespace ConsDTR.LAB.Host.Validation;

/// <summary>Finite reference enumeration plus real routed witness checks and deliberate semantic attacks.</summary>
public static class TaskValidation
{
    public static IReadOnlyList<string> Run()
    {
        var passed = new List<string>();
        ValidateNormalization();
        passed.Add("Typed immutable input normalization and finite opcode validation");
        var enumerated = ExhaustiveHorn();
        passed.Add($"Horn truth-model, direct closure and fair schedule agree for all {enumerated} three-atom instances");
        ValidateGenerators();
        passed.Add("Seeded Horn/graph/CSP generator semantics and isolated negative pressure");
        ValidateHornCertificates();
        passed.Add("Routed Horn positive/negative, empty-rule negative and semantic forgery rejection");
        ValidatePathCertificates();
        passed.Add("Routed paths/cuts, exact endpoint predicate, leaky cut and incomplete coverage rejection");
        ValidateCspCertificates();
        passed.Add("Routed finite CSP assignment, complete coverage, stale revision and UNSAT pressure rejection");
        ValidateCandidateAccumulation();
        passed.Add("Cross-page typed candidate accumulation, assignment replacement, delta forgery and stale-check rejection");
        return passed.AsReadOnly();
    }

    private static SessionOptions Options(ExecutionMode mode) => new()
    {
        Mode = mode, WorkerCount = 2, RegisterCount = 24, MaxSlots = 64,
        MaxWork = 5_000_000, MaxMicrosteps = 128, FinalGateWorkAllowance = 2_000_000
    };

    private static void ValidateNormalization()
    {
        var premises = new[] { "B", "A", "B" };
        var rule = new HornRule("r", premises, "C");
        premises[0] = "INJECTED";
        Require(rule.Premises.SequenceEqual(["A", "B"]) && rule.OriginalToCanonicalPremise.SequenceEqual([1, 0, 1]), "Horn premise normalization or copying failed.");
        var domain = new[] { 1, 0, 1 };
        var variable = new CspVariable("x", domain);
        domain[0] = 8;
        Require(variable.Domain.SequenceEqual([0, 1]), "Finite-domain input was not copied and normalized.");
        ExpectArgument(() => new HornRule("empty", [], "A"));
        ExpectArgument(() => new HornRule("invalid\ud800", ["A"], "B"));
        ExpectArgument(() => new HornProblem(["A"], ["B"], [], "A"));
        ExpectArgument(() => new GraphPathProblem(["s"], [], "s", "t"));
        ExpectArgument(() => new CspProblem([new CspVariable("x", [2])], [FiniteConstraint.Clause("clause", [new("x")])]));
        var emptyClause = new CspProblem([new CspVariable("x", [0, 1])], [FiniteConstraint.Clause("false", [])]);
        Require(emptyClause.Constraints[0].Literals.Length == 0, "Empty CNF clause must have exact false semantics.");
    }

    private static int ExhaustiveHorn()
    {
        string[] atoms = ["A", "B", "C"];
        var clauses = new List<(int Premises, int Head)>();
        for (var mask = 1; mask < 8; mask++)
        for (var head = 0; head < 3; head++) clauses.Add((mask, head));
        var ruleSets = new List<(int Premises, int Head)[]> { Array.Empty<(int Premises, int Head)>() };
        foreach (var clause in clauses) ruleSets.Add([clause]);
        for (var left = 0; left < clauses.Count; left++)
        for (var right = left + 1; right < clauses.Count; right++) ruleSets.Add([clauses[left], clauses[right]]);
        var count = 0;
        foreach (var rules in ruleSets)
        for (var facts = 0; facts < 8; facts++)
        for (var query = 0; query < 3; query++)
        {
            var input = new HornProblem(atoms, atoms.Where((_, index) => (facts & (1 << index)) != 0),
                rules.Select((clause, index) => new HornRule("r" + index, atoms.Where((_, atom) => (clause.Premises & (1 << atom)) != 0), atoms[clause.Head])), atoms[query]);
            var closure = Closure(facts, rules);
            var truthEntails = true;
            for (var model = 0; model < 8; model++)
                if ((model & facts) == facts && rules.All(clause => (model & clause.Premises) != clause.Premises || (model & (1 << clause.Head)) != 0) &&
                    (model & (1 << query)) == 0) { truthEntails = false; break; }
            Require(truthEntails == ((closure & (1 << query)) != 0), "Direct Horn closure disagrees with exact finite truth-model semantics.");
            long charge = 0;
            var plan = HornFairScheduler.BuildPlan(input, cost => charge = checked(charge + cost));
            Require(plan.Mode == ExecutionMode.FairFallback && charge > 0, "Fair closure is unmetered or incorrectly identified.");
            Require((plan.OutputBinding != "closed:answer") == truthEntails, "Fair scheduler answer differs from the independent truth oracle.");
            if (!truthEntails)
            {
                var candidate = plan.Actions.Single(action => action.Operation == HornTask.ProposeClosedSet).Candidate!;
                using var document = JsonDocument.Parse(candidate.Data);
                var members = document.RootElement.GetProperty("Members").EnumerateArray().Select(member => member.GetString()!).ToHashSet(StringComparer.Ordinal);
                var model = atoms.Select((atom, index) => members.Contains(atom) ? 1 << index : 0).Sum();
                Require((model & facts) == facts && (model & (1 << query)) == 0 &&
                    rules.All(clause => (model & clause.Premises) != clause.Premises || (model & (1 << clause.Head)) != 0), "Scheduled negative candidate is not a complete Horn model.");
            }
            count++;
        }
        Require(count == 5568, "Finite Horn enumeration changed unexpectedly.");
        return count;
    }

    private static int Closure(int facts, IEnumerable<(int Premises, int Head)> rules)
    {
        var closure = facts;
        for (;;)
        {
            var before = closure;
            foreach (var rule in rules)
                if ((closure & rule.Premises) == rule.Premises) closure |= 1 << rule.Head;
            if (before == closure) return closure;
        }
    }

    private static void ValidateGenerators()
    {
        foreach (var shape in Enum.GetValues<TaskProblemGenerator.HornShape>())
        foreach (var derivable in new[] { true, false })
        {
            var input = TaskProblemGenerator.Horn(4, 6, 12345, derivable, shape);
            var plan = HornFairScheduler.BuildPlan(input, _ => { });
            Require((plan.OutputBinding != "closed:answer") == derivable, "Generated Horn label does not match reference semantics.");
            Require(input.CanonicalInput == TaskProblemGenerator.Horn(4, 6, 12345, derivable, shape).CanonicalInput, "Seeded Horn input generation is not reproducible.");
        }
        foreach (var reachable in new[] { true, false })
        {
            var input = TaskProblemGenerator.Reachability(12, 71, reachable);
            var visited = new HashSet<string>(StringComparer.Ordinal) { input.Source };
            while (true)
            {
                var before = visited.Count;
                foreach (var edge in input.Edges) if (visited.Contains(edge.Source)) visited.Add(edge.Target);
                if (visited.Count == before) break;
            }
            Require(visited.Contains(input.Target) == reachable, "Generated path label does not match independent reachability.");
        }
        Require(CountBooleanWitnesses(TaskProblemGenerator.BooleanCsp(4, 5, 11)) > 0, "Planted SAT generator produced UNSAT.");
        Require(CountBooleanWitnesses(TaskProblemGenerator.BooleanCsp(4, 5, 11, false)) == 0, "CSP contradiction pressure is not UNSAT.");
    }

    private static int CountBooleanWitnesses(CspProblem problem)
    {
        var satisfying = 0;
        for (var mask = 0; mask < (1 << problem.Variables.Length); mask++)
        {
            var values = problem.Variables.Select((variable, index) => new { variable.Id, Value = (mask >> index) & 1 }).ToDictionary(value => value.Id, value => value.Value);
            if (problem.Constraints.All(constraint => constraint.Literals.Any(literal => values[literal.Variable] == (literal.Negated ? 0 : 1)))) satisfying++;
        }
        return satisfying;
    }

    private static void ValidateHornCertificates()
    {
        var input = new HornProblem(["A", "B", "C"], ["A"], [new("r1", ["A"], "B"), new("r2", ["B"], "C")], "C");
        var task = new HornTask(input);
        var runner = new DtrTaskPlanExecutor();
        var result = runner.RunHornFallback(input, Options(ExecutionMode.FairFallback));
        var certificate = RequireVerified(result);
        Require(!result.Verification!.IsNegative && result.Mode == ExecutionMode.FairFallback, "Horn derivation reported wrong polarity or learned identity.");
        var wrongConclusion = certificate.Proofs.Select(proof => proof.ProofId == certificate.Proofs[^1].ProofId ? proof with { ExactOutput = HornTask.Atom("B") } : proof).ToArray();
        RejectSemantics(task, certificate with { Proofs = wrongConclusion }, "Forged Horn conclusion was accepted.");
        var apply = certificate.Proofs.First(proof => proof.Operation == HornTask.ApplyRule);
        RejectSemantics(task, certificate with { Proofs = certificate.Proofs.Select(proof => proof.ProofId == apply.ProofId ? proof with { InputProofIds = [proof.InputProofIds[0]] } : proof).ToArray() }, "Horn action with missing premise was accepted.");
        RejectSemantics(task, certificate with { Task = certificate.Task with { CanonicalQuery = "\"A\"" } }, "A proof was accepted for a different exact query.");
        var negative = new HornProblem(["A", "B", "C"], ["A"], [new("r", ["A"], "B")], "C");
        var negativeResult = runner.RunHornFallback(negative, Options(ExecutionMode.FairFallback));
        var negativeCertificate = RequireVerified(negativeResult);
        Require(negativeResult.Verification!.IsNegative && negativeResult.Status == DtrResultStatus.VerifiedNegative, "Closed-set output did not preserve NOT_DERIVABLE semantics.");
        RejectOutputMutation(new HornTask(negative), negativeCertificate, node => node["CheckedInputIds"] = new JsonArray(), "Closed-set certificate with omitted coverage accepted.");
        RequireVerified(runner.RunHornFallback(new HornProblem(["A", "Q"], ["A"], [], "Q"), Options(ExecutionMode.FairFallback)));
        var immediate = runner.RunHornFallback(new HornProblem(["A"], ["A"], [], "A"), Options(ExecutionMode.FairFallback));
        RequireVerified(immediate);
        Require(immediate.Certificate!.Proofs.All(proof => proof.Operation == "INPUT_REF"), "An immutable input answer unexpectedly needed derived facts.");
    }

    private static void ValidatePathCertificates()
    {
        var input = new GraphPathProblem(["s", "a", "t"], [new("sa", "s", "a"), new("at", "a", "t"), new("as", "a", "s")], "s", "t", true);
        var task = new ReachabilityTask(input);
        var runner = new DtrTaskPlanExecutor();
        var result = runner.Execute(task, charge => TaskTeacherPlans.PathWitness(input, ["sa", "at"], charge), Options(ExecutionMode.OracleDiagnostic));
        var certificate = RequireVerified(result);
        RejectOutputMutation(task, certificate, node => node["Vertices"]!.AsArray()[^1] = "s", "Wrong final path endpoint accepted.");
        ExpectArgument(() => TaskTeacherPlans.PathWitness(input, ["sa", "as", "sa", "at"], _ => { }));
        var blocked = new GraphPathProblem(["s", "a", "t"], [new("sa", "s", "a"), new("as", "a", "s")], "s", "t");
        var blockedTask = new ReachabilityTask(blocked);
        var cutResult = runner.Execute(blockedTask, charge => TaskTeacherPlans.CutWitness(blocked, ["s", "a"], charge), Options(ExecutionMode.OracleDiagnostic));
        var cutCertificate = RequireVerified(cutResult);
        Require(cutResult.Verification!.IsNegative, "A closed cut was not identified as UNREACHABLE.");
        RejectOutputMutation(blockedTask, cutCertificate, node => node["CheckedInputIds"] = new JsonArray(), "Cut omitted-edge coverage accepted.");
        RequireNotVerified(runner.Execute(blockedTask, charge => TaskTeacherPlans.CutWitness(blocked, ["s"], charge), Options(ExecutionMode.OracleDiagnostic)), "A leaky graph cut was accepted.");
        RequireNotVerified(runner.Execute(blockedTask, charge => OmitLastCheck(TaskTeacherPlans.CutWitness(blocked, ["s", "a"], charge)), Options(ExecutionMode.OracleDiagnostic)), "Partial edge ledger was finalized.");
        var reflexive = new GraphPathProblem(["s"], [], "s", "s", true);
        RequireVerified(runner.Execute(new ReachabilityTask(reflexive), charge => TaskTeacherPlans.PathWitness(reflexive, [], charge), Options(ExecutionMode.OracleDiagnostic)));
    }

    private static void ValidateCspCertificates()
    {
        var input = new CspProblem([new("x", [0, 1]), new("y", [0, 1])],
            [FiniteConstraint.Equal("eq", "x", "y"), FiniteConstraint.Clause("clause", [new("x"), new("y", true)]),
             FiniteConstraint.Table("table", ["x", "y"], [new[] { 1, 1 }])]);
        KeyValuePair<string, int>[] witness = [new("x", 1), new("y", 1)];
        var task = new FiniteCspTask(input);
        var runner = new DtrTaskPlanExecutor();
        var result = runner.Execute(task, charge => TaskTeacherPlans.AssignmentWitness(input, witness, charge), Options(ExecutionMode.OracleDiagnostic));
        var certificate = RequireVerified(result);
        Require(!result.Verification!.IsNegative, "A SATISFIES witness must never be an UNSAT/negative answer.");
        RejectOutputMutation(task, certificate, node => node["CandidateRevision"] = 2, "Assignment checks reused for a different revision.");
        var proposal = certificate.Proofs.Single(proof => proof.Operation == FiniteCspTask.ProposeAssignment);
        var changed = JsonNode.Parse(proposal.ExactOutput.Data)!.AsObject(); changed["Revision"] = 2;
        var stale = certificate.Proofs.Select(proof => proof.ProofId == proposal.ProofId ? proof with { ExactOutput = proposal.ExactOutput with { Data = changed.ToJsonString() } } : proof).ToArray();
        RejectSemantics(task, certificate with { Proofs = stale }, "New assignment revision reused prior proof checks.");
        RequireNotVerified(runner.Execute(task, charge => OmitLastCheck(TaskTeacherPlans.AssignmentWitness(input, witness, charge)), Options(ExecutionMode.OracleDiagnostic)), "Partial constraint ledger was finalized.");
        RequireNotVerified(runner.Execute(task, charge => TaskTeacherPlans.AssignmentWitness(input, [new("x", 1)], charge), Options(ExecutionMode.OracleDiagnostic)), "Incomplete CSP assignment accepted.");
        var contradiction = new CspProblem([new("x", [0, 1])], [FiniteConstraint.Clause("yes", [new("x")]), FiniteConstraint.Clause("no", [new("x", true)])]);
        foreach (var value in new[] { 0, 1 })
            RequireNotVerified(runner.Execute(new FiniteCspTask(contradiction), charge => TaskTeacherPlans.AssignmentWitness(contradiction, [new("x", value)], charge), Options(ExecutionMode.OracleDiagnostic)), "Contradictory CSP accepted a SAT witness.");
    }

    private static void ValidateCandidateAccumulation()
    {
        var runner = new DtrTaskPlanExecutor();
        var options = Options(ExecutionMode.OracleDiagnostic) with { MaxSlots = 128, MaxWork = 20_000_000, FinalGateWorkAllowance = 4_000_000 };
        var names = Enumerable.Range(0, 34).Select(index => "v" + index.ToString("D2")).ToArray();
        var horn = new HornProblem(names.Append("query"), names, [new("r", [names[0]], names[^1])], "query");
        var hornTask = new HornTask(horn);
        var hornPages = new[]
        {
            TaskCandidates.HornClosedSet(horn, names.Take(12)),
            TaskCandidates.HornClosedSetDelta(horn, names.Skip(12).Take(12), 2),
            TaskCandidates.HornClosedSetDelta(horn, names.Skip(24), 3)
        };
        var hornPlan = PagedCandidatePlan(HornTask.ProposeClosedSet, HornTask.ExtendClosedSet,
            HornTask.BeginClosedSetCheck, HornTask.CheckClosedSetNext, HornTask.FinalizeClosedSetCheck, hornPages, [HornTask.RuleInputId("r")]);
        var hornCertificate = RequireVerified(runner.Execute(hornTask, charge => ChargeCandidatePlan(hornPlan, charge), options));
        ValidateAccumulatedValue(hornCertificate, HornTask.ExtendClosedSet, "Members", 34, 3);
        Require(hornCertificate.Manifest.ExactOutput.Type == "horn.not-derivable", "A candidate union was confused with a verified atom.");
        RequireNotVerified(runner.Execute(hornTask, _ => WithChangedCandidateAfterBegin(hornPlan, HornTask.ExtendClosedSet,
            TaskCandidates.HornClosedSetDelta(horn, [], 4)), options), "Horn checks were reused after the candidate revision changed.");
        RequireNotVerified(runner.Execute(hornTask, _ => ReplaceFirstDelta(hornPlan, hornPages[1] with { Type = "path.cut-delta" }), options), "A wrong typed delta was accepted.");
        RequireNotVerified(runner.Execute(hornTask, _ => ReplaceFirstDelta(hornPlan, hornPages[1] with { Trust = ValueTrust.Verified }), options), "A verified delta bypassed candidate typing.");
        RequireNotVerified(runner.Execute(hornTask, _ => ReplaceFirstDelta(hornPlan, TaskCandidates.HornClosedSetDelta(horn, names.Skip(12).Take(12), 1)), options), "A repeated candidate revision was accepted.");
        RequireNotVerified(runner.Execute(hornTask, _ => ReplaceFirstDelta(hornPlan, TaskCandidates.HornClosedSetDelta(horn, ["undeclared"], 2)), options), "An undeclared member was accepted in a delta.");
        var anotherHorn = new HornProblem(names.Append("query"), names, [], "query");
        RequireNotVerified(runner.Execute(hornTask, _ => ReplaceFirstDelta(hornPlan, TaskCandidates.HornClosedSetDelta(anotherHorn, names.Skip(12).Take(12), 2)), options), "A delta from another task fingerprint was accepted.");
        RejectAccumulationForgery(hornTask, hornCertificate, HornTask.ExtendClosedSet);

        var graph = new GraphPathProblem(names.Append("target"), [new("edge", names[0], names[^1])], names[0], "target");
        var graphTask = new ReachabilityTask(graph);
        var graphPages = new[]
        {
            TaskCandidates.GraphCut(graph, names.Skip(1).Take(11)),
            TaskCandidates.GraphCutDelta(graph, names.Take(1).Concat(names.Skip(12).Take(12)), 2),
            TaskCandidates.GraphCutDelta(graph, names.Skip(24).Concat(names.Take(1)), 3)
        };
        var graphPlan = PagedCandidatePlan(ReachabilityTask.ProposeCut, ReachabilityTask.ExtendCut,
            ReachabilityTask.BeginCutCheck, ReachabilityTask.CheckCutNext, ReachabilityTask.FinalizeCutCheck, graphPages, [ReachabilityTask.EdgeInputId("edge")]);
        var graphCertificate = RequireVerified(runner.Execute(graphTask, charge => ChargeCandidatePlan(graphPlan, charge), options));
        ValidateAccumulatedValue(graphCertificate, ReachabilityTask.ExtendCut, "Members", 34, 3);
        RequireNotVerified(runner.Execute(graphTask, _ => WithChangedCandidateAfterBegin(graphPlan, ReachabilityTask.ExtendCut,
            TaskCandidates.GraphCutDelta(graph, [], 4)), options), "Cut checks were reused after the candidate revision changed.");
        RequireNotVerified(runner.Execute(graphTask, _ => ReplaceFirstDelta(graphPlan, graphPages[1] with { Type = "horn.set-delta" }), options), "A non-cut delta was accepted by EXTEND_CUT.");
        RejectAccumulationForgery(graphTask, graphCertificate, ReachabilityTask.ExtendCut);

        var csp = new CspProblem(names.Select(name => new CspVariable(name, [0, 1])), [FiniteConstraint.Equal("eq", names[0], names[^1])]);
        var cspTask = new FiniteCspTask(csp);
        var cspPages = new[]
        {
            TaskCandidates.Assignment(csp, names.Take(12).Select((name, index) => new KeyValuePair<string, int>(name, index == 0 ? 0 : 1))),
            TaskCandidates.AssignmentDelta(csp, names.Skip(12).Take(12).Append(names[0]).Select(name => new KeyValuePair<string, int>(name, 1)), 2),
            TaskCandidates.AssignmentDelta(csp, names.Skip(24).Select(name => new KeyValuePair<string, int>(name, 1)), 3)
        };
        var cspPlan = PagedCandidatePlan(FiniteCspTask.ProposeAssignment, FiniteCspTask.ExtendAssignment,
            FiniteCspTask.BeginAssignmentCheck, FiniteCspTask.CheckAssignmentNext, FiniteCspTask.FinalizeAssignmentCheck, cspPages, [FiniteCspTask.ConstraintInputId("eq")]);
        var cspCertificate = RequireVerified(runner.Execute(cspTask, charge => ChargeCandidatePlan(cspPlan, charge), options));
        ValidateAccumulatedValue(cspCertificate, FiniteCspTask.ExtendAssignment, "Entries", 34, 3);
        using (var document = JsonDocument.Parse(cspCertificate.Proofs.Last(proof => proof.Operation == FiniteCspTask.ExtendAssignment).ExactOutput.Data))
            Require(document.RootElement.GetProperty("Entries")[0].GetProperty("Value").GetInt32() == 1, "An assignment delta did not replace the previous variable value.");
        RequireNotVerified(runner.Execute(cspTask, _ => WithChangedCandidateAfterBegin(cspPlan, FiniteCspTask.ExtendAssignment,
            TaskCandidates.AssignmentDelta(csp, [new(names[0], 0)], 4)), options), "Assignment checks were reused after a value/revision change.");
        RequireNotVerified(runner.Execute(cspTask, _ => ReplaceFirstDelta(cspPlan, TaskCandidates.AssignmentDelta(csp, [new(names[0], 2)], 2)), options), "A delta introduced an out-of-domain assignment value.");
        RequireNotVerified(runner.Execute(cspTask, _ => ReplaceFirstDelta(cspPlan, cspPages[1] with { Type = "horn.set-delta" }), options), "A non-assignment delta was accepted by EXTEND_ASSIGNMENT.");
        RejectAccumulationForgery(cspTask, cspCertificate, FiniteCspTask.ExtendAssignment);
    }

    private static TaskExecutionPlan PagedCandidatePlan(string propose, string extend, string begin, string next, string finalize,
        IReadOnlyList<ExactValue> pages, IReadOnlyList<string> inputIds)
    {
        var actions = new List<TaskPlannedAction> { new("candidate:0", propose, [], pages[0]) };
        for (var index = 1; index < pages.Count; index++)
            actions.Add(new("candidate:" + index, extend, ["candidate:" + (index - 1)], pages[index]));
        var candidate = "candidate:" + (pages.Count - 1);
        actions.Add(new("check:0", begin, [candidate]));
        for (var index = 0; index < inputIds.Count; index++)
            actions.Add(new("check:" + (index + 1), next, ["check:" + index, candidate, inputIds[index]]));
        actions.Add(new("answer", finalize, ["check:" + inputIds.Count]));
        return new(ExecutionMode.OracleDiagnostic, actions.ToImmutableArray(), "answer", "Explicit paged candidate witness");
    }

    private static TaskExecutionPlan ChargeCandidatePlan(TaskExecutionPlan plan, Action<long> charge)
    {
        charge(checked(plan.Actions.Length + plan.Actions.Sum(action => (long)(action.Candidate?.Data.Length ?? 0))));
        return plan;
    }

    private static TaskExecutionPlan ReplaceFirstDelta(TaskExecutionPlan plan, ExactValue delta)
    {
        var replaced = plan.Actions.First(action => action.Operation.StartsWith("EXTEND_", StringComparison.Ordinal));
        return plan with { Actions = plan.Actions.Select(action => action == replaced
            ? new TaskPlannedAction(action.ResultBinding, action.Operation, action.OperandBindings, delta, action.Argument) : action).ToImmutableArray() };
    }

    private static TaskExecutionPlan WithChangedCandidateAfterBegin(TaskExecutionPlan plan, string extend, ExactValue delta)
    {
        var nextIndex = Array.FindIndex(plan.Actions.ToArray(), action => action.Operation.StartsWith("CHECK_", StringComparison.Ordinal));
        var next = plan.Actions[nextIndex];
        var changed = new TaskPlannedAction("candidate:changed", extend, [next.OperandBindings[1]], delta);
        var invalidNext = new TaskPlannedAction(next.ResultBinding, next.Operation,
            [next.OperandBindings[0], changed.ResultBinding, next.OperandBindings[2]], next.Candidate, next.Argument);
        return plan with { Actions = plan.Actions.Take(nextIndex).Append(changed).Append(invalidNext).Concat(plan.Actions.Skip(nextIndex + 1)).ToImmutableArray() };
    }

    private static void ValidateAccumulatedValue(RuntimeCertificate certificate, string operation, string collection, int expectedCount, long revision)
    {
        var proof = certificate.Proofs.Last(proof => proof.Operation == operation);
        Require(proof.ExactOutput.Trust == ValueTrust.Candidate && proof.ProposedCandidate?.Trust == ValueTrust.Candidate &&
            TaskCandidates.RevisionOf(proof.ExactOutput) == revision, "Candidate accumulation changed trust or lost the immutable revision.");
        using var document = JsonDocument.Parse(proof.ExactOutput.Data);
        Require(document.RootElement.GetProperty(collection).GetArrayLength() == expectedCount, "Cross-page candidate accumulation omitted or duplicated members.");
    }

    private static void RejectAccumulationForgery(IKernelTask task, RuntimeCertificate certificate, string operation)
    {
        var extension = certificate.Proofs.First(proof => proof.Operation == operation);
        RejectSemantics(task, certificate with { Proofs = certificate.Proofs.Select(proof => proof.ProofId == extension.ProofId
            ? proof with { ProposedCandidate = null } : proof).ToArray() }, "An accumulation proof without its exact delta was accepted.");
        RejectSemantics(task, certificate with { Proofs = certificate.Proofs.Select(proof => proof.ProofId == extension.ProofId
            ? proof with { ExactOutput = proof.ExactOutput with { Trust = ValueTrust.Verified } } : proof).ToArray() }, "Accumulation promoted a candidate into verified data.");
        var modified = JsonNode.Parse(extension.ProposedCandidate!.Data)!.AsObject(); modified["Revision"] = 999;
        RejectSemantics(task, certificate with { Proofs = certificate.Proofs.Select(proof => proof.ProofId == extension.ProofId
            ? proof with { ProposedCandidate = proof.ProposedCandidate! with { Data = modified.ToJsonString() } } : proof).ToArray() }, "A forged delta revision was accepted.");
        RejectSemantics(task, certificate with { Proofs = certificate.Proofs.Select(proof => proof.ProofId == extension.ProofId
            ? proof with { InputProofIds = [] } : proof).ToArray() }, "A candidate extension without the prior transported candidate was accepted.");
    }

    private static TaskExecutionPlan OmitLastCheck(TaskExecutionPlan plan)
    {
        var omitted = plan.Actions[^2];
        var previous = plan.Actions[^3];
        var final = plan.Actions[^1];
        var actions = plan.Actions.Where(action => action != omitted && action != final).Append(
            new TaskPlannedAction(final.ResultBinding, final.Operation, [previous.ResultBinding], final.Candidate, final.Argument)).ToImmutableArray();
        return plan with { Actions = actions };
    }

    private static RuntimeCertificate RequireVerified(DtrInferenceResult result)
    {
        Require(result.IsVerified && result.Certificate is not null && result.Verification?.Accepted == true,
            "Expected a completely verified runtime certificate: " + result.Reason + "; " + result.Verification?.Reason);
        return result.Certificate!;
    }

    private static void RequireNotVerified(DtrInferenceResult result, string message) => Require(!result.IsVerified && result.Certificate is null, message);

    private static void RejectSemantics(IKernelTask task, RuntimeCertificate certificate, string message) =>
        Require(!IndependentVerifier.VerifySemantics(certificate, task.CreateIndependentVerifier(), 2_000_000).Accepted, message);

    private static void RejectOutputMutation(IKernelTask task, RuntimeCertificate certificate, Action<JsonObject> mutate, string message)
    {
        var node = JsonNode.Parse(certificate.Manifest.ExactOutput.Data)!.AsObject(); mutate(node);
        var output = certificate.Manifest.ExactOutput with { Data = node.ToJsonString() };
        var proofs = certificate.Proofs.Select(proof => proof.ProofId == certificate.Proofs[^1].ProofId ? proof with { ExactOutput = output } : proof).ToArray();
        RejectSemantics(task, certificate with { Manifest = certificate.Manifest with { ExactOutput = output }, Proofs = proofs }, message);
    }

    private static void ExpectArgument(Action action)
    {
        try { action(); } catch (ArgumentException) { return; }
        throw new InvalidOperationException("Expected malformed typed input or witness to be rejected.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
