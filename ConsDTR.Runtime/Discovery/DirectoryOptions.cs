namespace ConsDTR.Runtime.Discovery;

public sealed record DirectoryOptions
{
    public int MaxTargetsPerStep { get; init; } = 4;
    public int CandidatesPerTarget { get; init; } = 16;
    public int StructuralQuota { get; init; } = 4;
    public int ExistingOrPinnedQuota { get; init; } = 8;
    public int FairQuota { get; init; } = 4;
    public int FairTargetsPerStep { get; init; } = 1;
    public int MaxPins { get; init; } = 8;
    public int MaxIntentSteps { get; init; } = 16;
    public long MaxScanEntries { get; init; } = 1_000_000;
    public long MaxSnapshotEntries { get; init; } = 1_000_000;

    public void Validate()
    {
        if (MaxTargetsPerStep < 1 || CandidatesPerTarget < 1 || StructuralQuota < 0 ||
            ExistingOrPinnedQuota < 0 || FairQuota < 1 || FairTargetsPerStep < 1 ||
            FairTargetsPerStep > MaxTargetsPerStep || MaxPins < 0 || MaxIntentSteps < 1 ||
            (long)StructuralQuota + ExistingOrPinnedQuota + FairQuota > CandidatesPerTarget ||
            MaxScanEntries < 1 || MaxSnapshotEntries < 1)
            throw new ArgumentOutOfRangeException(nameof(DirectoryOptions));
    }
}
