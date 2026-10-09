using ConsDTR.Lib.Inference;
using ConsDTR.Runtime;

namespace ConsDTR.Lib.Evaluation;

/// <summary>Small numerical and mode-accounting checks; they do not initialize or train TorchSharp.</summary>
public static class DtrEvaluationValidation
{
    public static IReadOnlyList<string> Run()
    {
        var passed = new List<string>();
        var balanced = DtrBinaryStatistics.Wilson95(50, 100);
        Assert(Math.Abs(balanced.Lower - 0.4038315303659956) < 1e-12 && Math.Abs(balanced.Upper - 0.5961684696340044) < 1e-12);
        var zero = DtrBinaryStatistics.Wilson95(0, 10); var all = DtrBinaryStatistics.Wilson95(10, 10);
        Assert(zero.Lower == 0 && Math.Abs(zero.Upper - 0.2775327998628892) < 1e-12);
        Assert(all.Upper == 1 && Math.Abs(all.Lower - 0.7224672001371107) < 1e-12);
        passed.Add("Wilson95 matches known balanced and boundary binomial cases");
        var contract = new DtrBootstrapOptions { Seed = 42, Resamples = 1_000 };
        var identical = DtrBinaryStatistics.PairedBootstrap95([0, 1, 1], [0, 1, 1], contract);
        var advantage = DtrBinaryStatistics.PairedBootstrap95([1, 1, 1], [0, 0, 0], contract);
        Assert(identical.MeanCertifiedDifference == 0 && identical.Interval.Lower == 0 && identical.Interval.Upper == 0);
        Assert(advantage.MeanCertifiedDifference == 1 && advantage.Interval.Lower == 1 && advantage.Interval.Upper == 1);
        Assert(advantage == DtrBinaryStatistics.PairedBootstrap95([1, 1, 1], [0, 0, 0], contract));
        passed.Add("Paired bootstrap preserves exact paired zero/one differences and declared RNG seed");
        var observations = new[] { Observation("learned", ExecutionMode.LearnedOnly, false), Observation("fallback", ExecutionMode.FairFallback, true) };
        var groups = DtrEvaluationAggregator.Summarize(observations, overallByMode: true);
        Assert(groups.Single(group => group.Mode == ExecutionMode.LearnedOnly).CertifiedCoverage == 0);
        Assert(groups.Single(group => group.Mode == ExecutionMode.FairFallback).CertifiedCoverage == 1);
        Assert(groups.All(group => group.Instances == 1));
        passed.Add("Fallback successes cannot enter learned-only certified coverage");
        var metadata = new DtrEvaluationMetadata("ConsDTRv1", "Draft-2", "fixture-split", "fixture-corpus", null,
            "fixture-checkpoint", 1, [], "statistics fixture", "fixture-cost", new SessionOptions(),
            new ConsDTR.Runtime.Discovery.DirectoryOptions(), "fixture-budget", contract, DtrEditorMode.Learned,
            DtrMessageMode.Enabled, true, 1, "constant", 0, true, "statistics-only validation");
        var learnedPair = new[] { Observation("same-input", ExecutionMode.LearnedOnly, false) };
        var fallbackPair = new[] { Observation("same-input", ExecutionMode.FairFallback, true) };
        var left = new DtrEvaluationReport(metadata, learnedPair, [], []);
        var right = new DtrEvaluationReport(metadata, fallbackPair, [], []);
        var rejected = false;
        try { DtrPairedComparison.Compare(left, right); } catch (ArgumentException) { rejected = true; }
        Assert(rejected);
        passed.Add("Paired learned/fallback comparisons are rejected instead of sharing a success delta");
        var sat = new[]
        {
            Observation("sat-witness", ExecutionMode.LearnedOnly, true, true),
            Observation("sat-missed", ExecutionMode.LearnedOnly, false, true),
            Observation("unsat", ExecutionMode.LearnedOnly, false, false),
            Observation("unlabeled", ExecutionMode.LearnedOnly, false, null)
        };
        var csp = DtrEvaluationAggregator.Summarize(sat, overallByMode: true).Single();
        Assert(csp.KnownSatInstances == 2 && csp.SatWitnesses == 1 && csp.SatWitnessCoverage == 0.5 && csp.UnlabeledCspInstances == 1);
        Assert(csp.CertifiedCoverage == 0.25);
        passed.Add("CSP SATWitnessCoverage uses only known SAT labels and keeps mixed coverage separate");
        return passed.AsReadOnly();
    }

    private static DtrEvaluationCaseResult Observation(string id, ExecutionMode mode, bool certified, bool? sat = null) =>
        new(id, Guid.NewGuid(), "FiniteCSP", "FiniteCSP/v1", id, "ID", "BooleanCNF", 1, 42,
            mode, certified ? DtrResultStatus.VerifiedResult : DtrResultStatus.Unknown, certified, sat, "statistics-only fixture",
            1, 10, null, 1, 0, 1, null, 1, 0, 0, null);
    private static void Assert(bool value)
    { if (!value) throw new InvalidOperationException("Evaluation statistics/accounting regression."); }
}
