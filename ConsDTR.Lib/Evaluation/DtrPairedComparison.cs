namespace ConsDTR.Lib.Evaluation;

public static class DtrPairedComparison
{
    public static DtrPairedComparisonReport Compare(DtrEvaluationReport left, DtrEvaluationReport right,
        bool requireSameCheckpoint = true)
    {
        if (left.Cases.Count == 0 || left.Metadata.ObservedCorpusDigest != right.Metadata.ObservedCorpusDigest
            || left.Metadata.DataSplitFingerprint != right.Metadata.DataSplitFingerprint
            || left.Metadata.PairedBudgetSignature != right.Metadata.PairedBudgetSignature
            || left.Metadata.Bootstrap != right.Metadata.Bootstrap
            || requireSameCheckpoint && left.Metadata.ModelCheckpointDigest != right.Metadata.ModelCheckpointDigest)
            throw new ArgumentException("Paired comparisons require matching cases, declared splits, budgets, bootstrap contract and requested checkpoint identity.");
        var orderedLeft = left.Cases.OrderBy(row => row.CaseId, StringComparer.Ordinal).ToArray();
        var orderedRight = right.Cases.OrderBy(row => row.CaseId, StringComparer.Ordinal).ToArray();
        if (orderedLeft.Length != orderedRight.Length || orderedLeft.Select(row => row.Mode).Distinct().Count() != 1
            || !orderedLeft.Select(row => (row.CaseId, row.TaskDefinitionDigest, row.Mode, row.OracleIsSatisfiable))
                .SequenceEqual(orderedRight.Select(row => (row.CaseId, row.TaskDefinitionDigest, row.Mode, row.OracleIsSatisfiable))))
            throw new ArgumentException("Each pair must bind the same task, label and execution mode; fallback and learned runs cannot be mixed.");
        var estimate = DtrBinaryStatistics.PairedBootstrap95(orderedLeft.Select(row => row.Certified ? 1 : 0).ToArray(),
            orderedRight.Select(row => row.Certified ? 1 : 0).ToArray(), left.Metadata.Bootstrap);
        return new(orderedLeft[0].Mode, left.Metadata.ObservedCorpusDigest, left.Metadata.ModelCheckpointDigest,
            right.Metadata.ModelCheckpointDigest, left.Metadata.PairedBudgetSignature, estimate,
            $"Editor={left.Metadata.EditorIntervention};Messages={left.Metadata.MessageIntervention}",
            $"Editor={right.Metadata.EditorIntervention};Messages={right.Metadata.MessageIntervention}",
            "Paired execution measurement. Architecture gains require separately trained controls; neural-message contribution requires its own interventions.");
    }
}
