namespace ConsDTR.Runtime;

public enum SlotKind { Observation, Register, Worker, Goal }

public sealed record SlotState(SlotHandle Handle, SlotKind Kind, bool Active,
    long? RecordId = null, long BindingRevision = 0, long MetadataRevision = 0);
