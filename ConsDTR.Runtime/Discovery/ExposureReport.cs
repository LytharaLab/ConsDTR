namespace ConsDTR.Runtime.Discovery;

public sealed record ExposureReport(
    long Epoch,
    long EpochExposureBound,
    bool FullEpochFitsBudget,
    long SnapshotEntries,
    long ScannedEntries,
    long Tombstones,
    long PinHits,
    long PinInvalidations,
    bool Complete);
