namespace ConsDTR.Lib.Models;

public enum DtrEditKind
{
    EndEdits = 0,
    NoOp = 1,
    AddEdge = 2,
    RemoveEdge = 3,
    RetypeEdge = 4,
    ActivateSlot = 5,
    ResetWorker = 6
}
