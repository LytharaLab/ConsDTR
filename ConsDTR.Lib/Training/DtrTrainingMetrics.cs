namespace ConsDTR.Lib.Training;

public sealed record DtrTrainingMetrics(long Update, double Loss, double PolicyLoss,
    double ValueLoss, double Entropy, int EpisodeSteps, string Objective, bool FullBptt,
    bool GradientClipped);
