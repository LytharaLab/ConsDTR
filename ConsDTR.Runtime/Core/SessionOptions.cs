namespace ConsDTR.Runtime;

public enum ExecutionMode { LearnedOnly, FairFallback, OracleDiagnostic }

public sealed record SessionOptions
{
    public Guid SessionId { get; init; } = Guid.NewGuid();
    public string ModelCheckpointDigest { get; init; } = "untrained";
    public ExecutionMode Mode { get; init; } = ExecutionMode.LearnedOnly;
    public int WorkerCount { get; init; } = 8;
    public int RegisterCount { get; init; } = 128;
    public int MaxSlots { get; init; } = 256;
    public int MaxInDegree { get; init; } = 8;
    public int MaxOutDegree { get; init; } = 8;
    public int MaxEdgeEditsPerStep { get; init; } = 8;
    public int MaxSlotOperationsPerStep { get; init; } = 2;
    public int MaxEditedEndpointsPerStep { get; init; } = 16;
    public int MaxControlReadsPerStep { get; init; } = 68;
    public long MaxMicrosteps { get; init; } = 512;
    public long MaxWork { get; init; } = 100_000_000;
    public long MaxJournalEvents { get; init; } = 262_144;
    public long MaxJournalBytes { get; init; } = 67_108_864;
    public long MaxProofs { get; init; } = 4096;
    public long MaxExactBytes { get; init; } = 16_000_000;
    public int MaxValueBytes { get; init; } = 1_000_000;
    public int MaxIntegerBits { get; init; } = 64;
    public long FinalGateWorkAllowance { get; init; } = 50_000;
    public long MaxExportBytes { get; init; } = 128_000_000;
    public string ExportAuthorityId { get; init; } = "local-runtime";
    public string? JournalPath { get; init; }
}
