using ConsDTR.Lib.Training;

namespace ConsDTR.Lib.Models;

public sealed record DtrCheckpointManifest
{
    public string CheckpointId { get; init; } = Guid.NewGuid().ToString("N");
    public DtrModelConfig Model { get; init; } = new();
    public string WeightsSha256 { get; init; } = "";
    public string KernelSemanticVersion { get; init; } = "ConsDTRv1-Kernel-Draft-2";
    public string InputTypeSchemaVersion { get; init; } = "ConsDTRv1-IR-Draft-2";
    public string JournalSchemaVersion { get; init; } = "ConsDTRv1-Replay-Draft-2";
    public DtrTrainingRecipe TrainingRecipe { get; init; } = new();
    public string DataSplitFingerprint { get; init; } = "unassigned";
    public long CompletedUpdates { get; init; }
    public bool CapabilityValidated { get; init; }

    public void Validate()
    {
        if (Model is null || TrainingRecipe is null || string.IsNullOrWhiteSpace(WeightsSha256))
            throw new InvalidDataException("Checkpoint model, training recipe and weight digest must be present.");
        Model.Validate();
        TrainingRecipe.Validate();
        if (string.IsNullOrWhiteSpace(CheckpointId) || WeightsSha256.Length != 64 ||
            WeightsSha256.Any(character => !Uri.IsHexDigit(character)) ||
            KernelSemanticVersion != "ConsDTRv1-Kernel-Draft-2" || InputTypeSchemaVersion != "ConsDTRv1-IR-Draft-2" ||
            JournalSchemaVersion != "ConsDTRv1-Replay-Draft-2" || string.IsNullOrWhiteSpace(DataSplitFingerprint) ||
            CompletedUpdates < 0)
            throw new InvalidDataException("Incomplete or unsupported neural checkpoint manifest.");
    }
}
