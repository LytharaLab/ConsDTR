namespace ConsDTR.Runtime;

public sealed record KernelAction(SlotHandle Worker, string Operation,
    IReadOnlyList<ReadReceipt> ReadReceipts, WriteReceipt WriteReceipt,
    ExactValue? Candidate = null, string? Argument = null);
