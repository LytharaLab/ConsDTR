namespace ConsDTR.Runtime;

internal sealed record GenesisPayload(string ArchitectureId, string SpecRevision, string JournalSchema,
    string KernelVersion, TaskDefinition Task, SessionOptions Options, RuntimeState InitialState);
