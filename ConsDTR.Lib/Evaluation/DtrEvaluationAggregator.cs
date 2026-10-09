using ConsDTR.Lib.Inference;
using ConsDTR.Runtime;

namespace ConsDTR.Lib.Evaluation;

public static class DtrEvaluationAggregator
{
    public static IReadOnlyList<DtrEvaluationGroup> Summarize(IReadOnlyList<DtrEvaluationCaseResult> observations, bool overallByMode = false)
    {
        return observations.GroupBy(row => (row.Mode, Task: overallByMode ? "ALL" : row.TaskSemanticVersion,
                Split: overallByMode ? "ALL" : row.Split, Family: overallByMode ? "ALL" : row.Family, Scale: overallByMode ? 0 : row.Scale))
            .OrderBy(group => group.Key.Mode).ThenBy(group => group.Key.Task, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Split, StringComparer.Ordinal).ThenBy(group => group.Key.Family, StringComparer.Ordinal).ThenBy(group => group.Key.Scale)
            .Select(group => SummarizeGroup(group.Key.Mode, group.Key.Task, group.Key.Split, group.Key.Family, group.Key.Scale, group.ToArray())).ToArray();
    }

    private static DtrEvaluationGroup SummarizeGroup(ExecutionMode mode, string semantics, string split,
        string family, int scale, DtrEvaluationCaseResult[] rows)
    {
        if (!Enum.IsDefined(mode) || rows.Any(x => x.Mode != mode)) throw new ArgumentException("Evaluation modes cannot share a denominator.");
        if (rows.Any(row => row.Certified != (row.Status is DtrResultStatus.VerifiedResult or DtrResultStatus.VerifiedNegative)
            || row.IsCsp && row.Status == DtrResultStatus.VerifiedNegative
            || row.Microsteps < 0 || row.WorkCost < 0 || row.JournalBytes < 0 || row.JournalEvents < 0
            || !double.IsFinite(row.RunElapsedMilliseconds) || row.RunElapsedMilliseconds < 0))
            throw new ArgumentException("Coverage records must use verified protocol statuses and finite nonnegative measured costs; v1 CSP has no negative certificate.");
        var certified = rows.Count(row => row.Certified); var unknown = rows.Count(row => row.Status == DtrResultStatus.Unknown);
        var sat = rows.Where(row => row.IsCsp && row.OracleIsSatisfiable == true).ToArray();
        var witnesses = sat.Count(row => row.PositiveCertified);
        var latencies = rows.Select(row => row.RunElapsedMilliseconds).Order().ToArray();
        return new(mode, semantics, split, family, scale, rows.Length, certified, (double)certified / rows.Length,
            DtrBinaryStatistics.Wilson95(certified, rows.Length), unknown, (double)unknown / rows.Length,
            rows.Count(row => row.Status == DtrResultStatus.ResourceOrNumericFailure), rows.Count(row => row.Status == DtrResultStatus.Unsupported),
            rows.Count(row => row.Status == DtrResultStatus.UnverifiedCandidate), sat.Length, witnesses,
            sat.Length == 0 ? null : (double)witnesses / sat.Length, sat.Length == 0 ? null : DtrBinaryStatistics.Wilson95(witnesses, sat.Length),
            rows.Count(row => row.IsCsp && row.OracleIsSatisfiable is null), rows.Count(row => row.OracleLabelConflict),
            rows.Sum(row => row.Microsteps), rows.Sum(row => row.WorkCost), rows.Sum(row => row.DirectoryScannedEntries ?? 0),
            rows.Count(row => row.DirectoryScannedEntries.HasValue), rows.Sum(row => row.EditedEdges), rows.Sum(row => row.RejectedActions),
            rows.Sum(row => row.ProofCount), rows.Sum(row => row.JournalBytes ?? 0), rows.Count(row => row.JournalBytes.HasValue),
            latencies.Average(), Quantile(latencies, 0.5), Quantile(latencies, 0.95), rows.Sum(row => row.EvaluationVerificationMilliseconds),
            rows.Sum(row => row.EvaluationVerificationWork), rows.Sum(row => row.JournalEvents));
    }

    private static double Quantile(double[] sorted, double probability)
    {
        var position = probability * (sorted.Length - 1); var lower = (int)Math.Floor(position); var upper = (int)Math.Ceiling(position);
        return sorted[lower] + (position - lower) * (sorted[upper] - sorted[lower]);
    }
}
