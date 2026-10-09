namespace ConsDTR.Runtime.Discovery;

public sealed record DirectoryPage(
    CandidateHeader Target,
    IReadOnlyList<CandidateHeader> Candidates,
    long Epoch,
    int ScannedEntries,
    int Tombstones,
    bool IsFairService);
