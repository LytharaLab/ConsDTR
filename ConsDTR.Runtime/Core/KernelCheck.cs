namespace ConsDTR.Runtime;

public sealed record KernelCheck(bool Accepted, string Reason, ExactValue? Output = null, string? SemanticInputId = null)
{
    public static KernelCheck Accept(ExactValue output, string? semanticInputId = null) => new(true, "Accepted", output, semanticInputId);
    public static KernelCheck Reject(string reason) => new(false, reason);
}
