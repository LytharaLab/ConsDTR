namespace ConsDTR.Lib.Training;

public sealed record DtrTrainingRecipe
{
    public string Optimizer { get; init; } = "Adam";
    public double LearningRate { get; init; } = 0.001;
    public double ValueLossWeight { get; init; } = 0.5;
    public double EntropyWeight { get; init; } = 0.01;
    public double? GradientNormClip { get; init; }
    public int MaximumEpisodeSteps { get; init; } = 512;
    public int? TruncatedBpttSteps { get; init; }
    public int BatchSize { get; init; } = 1;
    public int MaximumInputSlots { get; init; } = 256;
    public ulong Seed { get; init; } = 1;
    public string TeacherVersion { get; init; } = "caller-declared";
    public string SplitFingerprint { get; init; } = "unassigned";
    public string CurriculumStage { get; init; } = "A-LocalOperation";
    public double CertificateReward { get; init; } = 1;
    public double StepCostWeight { get; init; } = 0.001;
    public double EditCostWeight { get; init; } = 0.001;
    public double ExactCheckCostWeight { get; init; } = 0.001;
    public double ExactDataCostWeight { get; init; } = 0.000001;

    public void Validate()
    {
        if (Optimizer != "Adam" || !double.IsFinite(LearningRate) || LearningRate <= 0 ||
            !double.IsFinite(ValueLossWeight) || ValueLossWeight < 0 ||
            !double.IsFinite(EntropyWeight) || EntropyWeight < 0 || MaximumEpisodeSteps < 1 ||
            BatchSize < 1 || MaximumInputSlots < 1 || TruncatedBpttSteps is <= 0 ||
            GradientNormClip is { } clip && (!double.IsFinite(clip) || clip <= 0) ||
            new[] { CertificateReward, StepCostWeight, EditCostWeight, ExactCheckCostWeight, ExactDataCostWeight }
                .Any(value => !double.IsFinite(value) || value < 0))
            throw new ArgumentException("Invalid training recipe.");
    }

    public bool UsesFullUnbiasedScoreGradient => TruncatedBpttSteps is null && GradientNormClip is null;
}
