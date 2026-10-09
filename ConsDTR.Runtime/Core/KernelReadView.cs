namespace ConsDTR.Runtime;

public sealed record DeliveredOperand(ExactValue Value, long ProofId, string InputId, ReadReceipt Receipt);

public sealed class KernelReadView
{
    public IReadOnlyList<DeliveredOperand> Operands { get; }
    public WriteReceipt WriteAuthorization { get; }
    public string TaskId { get; }
    public string SemanticVersion { get; }
    internal KernelReadView(IReadOnlyList<DeliveredOperand> operands, WriteReceipt write, TaskDefinition definition)
    {
        Operands = Array.AsReadOnly(operands.ToArray());
        WriteAuthorization = write;
        TaskId = definition.TaskId;
        SemanticVersion = definition.SemanticVersion;
    }
}
