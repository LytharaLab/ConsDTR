using ConsDTR.Lib.Evaluation;
using ConsDTR.Lib.Inference;
using ConsDTR.Lib.Models;
using ConsDTR.Runtime;
using ConsDTR.Runtime.Tasks;
using static TorchSharp.torch;

namespace ConsDTR.LAB.Host.Validation;

/// <summary>Real three-task evaluation integration. Random fixture weights do not establish learned task capability.</summary>
public static class EvaluationValidation
{
    public static IReadOnlyList<string> Run(string device = "cpu")
    {
        var passed = new List<string>();
        random.manual_seed(731);
        using var model = new DtrNeuralModel(new DtrModelConfig { HiddenDim = 16, RelationRank = 4 }, device);
        model.FreezeForInference();
        var checkpoint = DtrCheckpointStore.ComputeModelDigest(model);
        var runner = new DtrEvaluationRunner(model);
        var positive = new HornTask(new HornProblem(["A", "B", "C"], ["A"],
            [new("r1", ["A"], "B"), new("r2", ["B"], "C")], "C"));
        var negative = new HornTask(new HornProblem(["A", "B", "C"], ["A"], [new("r", ["A"], "B")], "C"));
        DtrEvaluationCase[] hornCases =
        [
            new(positive, "Integration", "HornPositive", 1, 71),
            new(negative, "Integration", "HornNegative", 1, 72)
        ];
        var fair = runner.Evaluate(hornCases, Options(ExecutionMode.FairFallback));
        Require(fair.Cases.Count == 2 && fair.Cases.All(row => row.Certified && row.Mode == ExecutionMode.FairFallback),
            "Horn evaluation did not independently certify both fair-fallback cases.");
        Require(fair.Cases[0].Status == DtrResultStatus.VerifiedResult && fair.Cases[1].Status == DtrResultStatus.VerifiedNegative,
            "Horn positive/NOT_DERIVABLE polarity was lost during evaluation.");
        Require(fair.OverallByMode.Single().CertifiedCoverage == 1 && fair.OverallByMode.Single().Mode == ExecutionMode.FairFallback,
            "Fallback results were combined with a learned-only denominator.");
        passed.Add("Actual evaluation independently certifies positive and negative Horn fair-fallback cases");

        var path = new ReachabilityTask(new GraphPathProblem(["s", "a", "t"],
            [new("sa", "s", "a"), new("at", "a", "t"), new("as", "a", "s")], "s", "t", true));
        var cut = new ReachabilityTask(new GraphPathProblem(["s", "a", "t"],
            [new("sa", "s", "a"), new("as", "a", "s")], "s", "t"));
        var csp = new FiniteCspTask(new CspProblem([new("x", [0, 1]), new("y", [0, 1])],
            [FiniteConstraint.Equal("eq", "x", "y"), FiniteConstraint.Clause("clause", [new("x"), new("y", true)]),
                FiniteConstraint.Table("table", ["x", "y"], [new[] { 1, 1 }])]));
        DtrEvaluationCase[] oracleCases =
        [
            new(path, "Integration", "PathWitness", 1, 81),
            new(cut, "Integration", "CutWitness", 1, 82),
            new(csp, "Integration", "SatWitness", 1, 83, IsSatisfiable: true)
        ];
        var oracleOptions = Options(ExecutionMode.OracleDiagnostic) with { ReferencePlanFactory = DiagnosticPlan };
        var oracle = runner.Evaluate(oracleCases, oracleOptions);
        Require(oracle.Cases.All(row => row.Certified && row.Mode == ExecutionMode.OracleDiagnostic),
            "Explicit graph/CSP witness evaluation failed the complete independent gate.");
        Require(oracle.Cases[0].Status == DtrResultStatus.VerifiedResult && oracle.Cases[1].Status == DtrResultStatus.VerifiedNegative
            && oracle.Cases[2].Status == DtrResultStatus.VerifiedResult, "Path/cut/CSP exact predicates were mislabeled.");
        var satGroup = oracle.Groups.Single(group => group.TaskSemanticVersion == FiniteCspTask.Semantics);
        Require(satGroup.KnownSatInstances == 1 && satGroup.SatWitnesses == 1 && satGroup.SatWitnessCoverage == 1
            && satGroup.OracleLabelConflicts == 0, "CSP SAT witness evaluation used the wrong label denominator.");
        Require(oracle.OverallByMode.Single().Mode == ExecutionMode.OracleDiagnostic,
            "Oracle diagnostic witnesses were reported as learned successes.");
        passed.Add("Actual OracleDiagnostic evaluation certifies a graph path, closed cut and finite CSP SAT witness");

        foreach (var report in new[] { fair, oracle })
        {
            Require(report.Metadata.ModelCheckpointDigest == checkpoint && report.Metadata.ActualCheckpointCount == 1
                && report.Metadata.CallerDeclaredTrainingSeeds.Count == 0 && !report.Metadata.PolicyInterventionsApplied,
                "Fixture checkpoint or untrained seed provenance was fabricated.");
            Require(report.Cases.Select(row => row.SessionId).Distinct().Count() == report.Cases.Count,
                "Evaluation cases reused runtime session identity.");
            Require(report.Cases.All(row => row.WorkCost > 0 && row.ProofCount > 0 && row.JournalBytes > 0 && row.JournalEvents > 0
                && row.EvaluationVerificationWork > 0 && row.CertificateDigest is { Length: 64 }
                && double.IsFinite(row.RunElapsedMilliseconds) && row.RunElapsedMilliseconds >= 0),
                "Actual routed evaluation omitted work, complete journal, evidence, external verification or latency measurements.");
            var total = report.OverallByMode.Single();
            Require(total.TotalWork == report.Cases.Sum(row => row.WorkCost)
                && total.KnownJournalBytes == report.Cases.Sum(row => row.JournalBytes!.Value)
                && total.TotalJournalEvents == report.Cases.Sum(row => row.JournalEvents)
                && total.JournalMeasurements == report.Cases.Count, "Evaluation aggregation changed measured run costs.");
            Require(!string.IsNullOrEmpty(report.Metadata.DataSplitFingerprint) && report.Metadata.ObservedCorpusDigest.Length == 64
                && report.Metadata.PairedBudgetSignature.Length == 64 && !string.IsNullOrEmpty(report.Metadata.CostScheduleId),
                "Evaluation report lost its declared split, actual corpus or cost schedule binding.");
            using var parsed = System.Text.Json.JsonDocument.Parse(report.Serialize());
            Require(parsed.RootElement.GetProperty("Cases").GetArrayLength() == report.Cases.Count,
                "Research JSON serialization changed actual case membership.");
        }
        passed.Add("Real evaluations preserve checkpoint identity and aggregate measured work, journal bytes, evidence and latency");

        var unlabeled = runner.Evaluate([oracleCases[2] with { IsSatisfiable = null }], oracleOptions);
        var unlabeledSat = unlabeled.Groups.Single();
        Require(unlabeled.Cases.Single().Certified && unlabeled.Cases.Single().TaskDefinitionDigest == oracle.Cases[2].TaskDefinitionDigest
            && unlabeledSat.KnownSatInstances == 0 && unlabeledSat.UnlabeledCspInstances == 1 && unlabeledSat.SatWitnessCoverage is null,
            "An oracle label leaked into task input or an unlabeled CSP was counted in SATWitnessCoverage.");
        passed.Add("CSP oracle labels remain evaluation metadata and missing labels stay outside the SAT denominator");

        DtrEvaluationCase[] learnedCases = [hornCases[0]];
        var learnedTemplate = Options(ExecutionMode.LearnedOnly);
        var learnedOptions = learnedTemplate with
        {
            Inference = learnedTemplate.Inference with
            {
                Editor = DtrEditorMode.Frozen,
                Runtime = learnedTemplate.Inference.Runtime with { MaxMicrosteps = 1 }
            }
        };
        var frozen = runner.Evaluate(learnedCases, learnedOptions);
        Require(frozen.Cases.Single().Mode == ExecutionMode.LearnedOnly && !frozen.Cases.Single().Certified
            && frozen.Cases.Single().Status == DtrResultStatus.Unknown && frozen.Cases.Single().JournalBytes > 0,
            "A one-step empty frozen graph obtained a hidden teacher result or omitted UNKNOWN measurements.");
        Require(frozen.OverallByMode.Single().CertifiedCoverage == 0 && frozen.OverallByMode.Single().UnknownFraction == 1
            && frozen.Metadata.PolicyInterventionsApplied && frozen.Cases.Single().DirectoryScannedEntries > 0,
            "Learned-only mode, bounded exposure or UNKNOWN accounting was changed by solver results.");
        passed.Add("One-step learned-only mode records actual UNKNOWN/exposure costs without borrowing solver coverage");

        var randomLegal = runner.Evaluate(learnedCases, learnedOptions with
        { Inference = learnedOptions.Inference with { Editor = DtrEditorMode.RandomLegal } });
        var paired = DtrPairedComparison.Compare(frozen, randomLegal);
        Require(paired.Mode == ExecutionMode.LearnedOnly && paired.PairedBudgetSignature == frozen.Metadata.PairedBudgetSignature
            && frozen.Metadata.PairedBudgetSignature == randomLegal.Metadata.PairedBudgetSignature
            && paired.Estimate.Pairs == 1 && paired.Estimate.MeanCertifiedDifference == 0
            && paired.Estimate.Interval.Lower == 0 && paired.Estimate.Interval.Upper == 0
            && paired.Estimate.BootstrapSeed == learnedOptions.Bootstrap.Seed,
            "Actual paired editor reports changed common resource budget, task pairing or predeclared bootstrap sampling.");
        passed.Add("Actual frozen/random-legal editor measurements pair under the same checkpoint, instances and declared budget");

        var differentBudget = runner.Evaluate(learnedCases, learnedOptions with
        { Inference = learnedOptions.Inference with { Runtime = learnedOptions.Inference.Runtime with { MaxWork = learnedOptions.Inference.Runtime.MaxWork - 100 } } });
        ExpectArgument(() => DtrPairedComparison.Compare(frozen, differentBudget));
        ExpectArgument(() => DtrPairedComparison.Compare(frozen, fair));
        ExpectArgument(() => runner.Evaluate(learnedCases, learnedOptions with { ReferencePlanFactory = DiagnosticPlan }));
        ExpectArgument(() => runner.Evaluate(oracleCases, oracleOptions with
        { Inference = oracleOptions.Inference with { Messages = DtrMessageMode.Disabled } }));
        Require(model.IsFrozen && checkpoint == DtrCheckpointStore.ComputeModelDigest(model),
            "Evaluation altered the frozen model parameters.");
        passed.Add("Actual report comparisons reject mismatched budgets/modes and reference-plan intervention bypasses");
        return passed.AsReadOnly();
    }

    private static DtrEvaluationOptions Options(ExecutionMode mode) => new()
    {
        DataSplitFingerprint = "evaluation-integration-fixtures/v1",
        CheckpointId = "random-weight-integration-fixture;untrained",
        CallerDeclaredTrainingSeeds = [],
        Bootstrap = new() { Seed = 193, Resamples = 1_000 },
        Inference = new()
        {
            SamplingSeed = 271, Deterministic = true, Exploration = 0,
            Runtime = new()
            {
                Mode = mode, WorkerCount = 2, RegisterCount = 24, MaxSlots = 64,
                MaxMicrosteps = 64, MaxWork = 20_000_000, FinalGateWorkAllowance = 2_000_000
            }
        }
    };

    private static TaskExecutionPlan DiagnosticPlan(DtrEvaluationCase sample, Action<long> charge) => sample.Task switch
    {
        ReachabilityTask path when path.Problem.Edges.Any(edge => edge.Id == "at") =>
            TaskTeacherPlans.PathWitness(path.Problem, ["sa", "at"], charge),
        ReachabilityTask cut => TaskTeacherPlans.CutWitness(cut.Problem, ["s", "a"], charge),
        FiniteCspTask csp => TaskTeacherPlans.AssignmentWitness(csp.Problem, [new("x", 1), new("y", 1)], charge),
        _ => throw new ArgumentException("No supplied diagnostic witness for this integration task.")
    };

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Evaluation integration failed: " + message); }
    private static void ExpectArgument(Action action)
    {
        try { action(); } catch (ArgumentException) { return; }
        throw new InvalidOperationException("Expected an invalid evaluation/paired comparison contract to be rejected.");
    }
}
