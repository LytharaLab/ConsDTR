namespace ConsDTR.Runtime;

public sealed record CertificateManifest(string ArchitectureId, string SpecRevision, string JournalSchema,
    Guid SessionId, string GenesisDigest, string InputQueryDigest, string KernelVersion,
    string TaskVersion, string ModelCheckpointDigest, ExecutionMode ExecutionMode,
    ExactValue ExactOutput, string ProofRootDigest, long FirstSeq, long LastSeq, long EventCount,
    long TerminalTopologyRevision, long TerminalExactStateRevision, string TerminalStateDigest,
    string TerminalEventDigest, long DeclaredWorkCost, string ExportAuthorityId, string EndAnchorId);
