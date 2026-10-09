namespace ConsDTR.Runtime;

internal enum RuntimePhase { Idle, Editing, Transporting, Transported, Committed, Prepared, Sealed }

internal sealed record RuntimeState
{
    public long MicrostepId { get; init; } = -1;
    public long TopologyRevision { get; init; }
    public long ExactStateRevision { get; init; }
    public SlotState[] Slots { get; init; } = [];
    public DataEdge[] Edges { get; init; } = [];
    public ExactRecord[] Records { get; init; } = [];
    public ProofEvent[] Proofs { get; init; } = [];
    public long WorkCost { get; init; }
    public long StepsUsed { get; init; }
    public long ExactBytes { get; init; }
    public long NextEdgeId { get; init; }
    public long ControlViewId { get; init; }
    public SlotHandle[] ControlReadSet { get; init; } = [];
    public bool ProbeOpen { get; init; }
    public long TransportSnapshotSeq { get; init; } = -1;
    public int EdgeEditsThisStep { get; init; }
    public int SlotOperationsThisStep { get; init; }
    public int ControlReadsThisStep { get; init; }
    public SlotHandle[] EditedEndpoints { get; init; } = [];
    public string AuditDigest { get; init; } = string.Empty;
    public ExecutionMode Mode { get; init; }
    public RuntimePhase Phase { get; init; }
    public string Digest() => DtrCanonical.Digest(this);
}
