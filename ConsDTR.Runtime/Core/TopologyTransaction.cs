namespace ConsDTR.Runtime;

public enum TopologyEditKind { AddEdge, RemoveEdge, RetypeEdge, ActivateSlot, ResetWorker, NoOp }

public sealed record TopologyEdit(TopologyEditKind Kind, SlotHandle Source = default,
    SlotHandle Target = default, EdgeRole Role = EdgeRole.NeuralMessage, string Port = "message", long EdgeId = -1);

public sealed record TopologyTransaction(IReadOnlyList<TopologyEdit> Edits);
