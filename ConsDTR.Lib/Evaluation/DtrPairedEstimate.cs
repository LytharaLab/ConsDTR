namespace ConsDTR.Lib.Evaluation;

public sealed record DtrPairedEstimate(double MeanCertifiedDifference, DtrConfidenceInterval Interval,
    int Pairs, int BootstrapSeed, int BootstrapResamples, string BootstrapAlgorithm);
