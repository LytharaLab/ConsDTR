namespace ConsDTR.Runtime.Tasks;

internal static class TaskOperands
{
    public static DeliveredOperand One(KernelReadView view, string type, bool verified = true)
    {
        var matches = view.Operands.Where(operand => operand.Value.Type == type).ToArray();
        if (matches.Length != 1 || (verified && matches[0].Value.Trust != ValueTrust.Verified))
            throw new ArgumentException($"Exactly one {(verified ? "verified " : "")}{type} operand is required.");
        return matches[0];
    }

    public static void Count(KernelReadView view, int expected)
    {
        if (view.Operands.Count != expected)
            throw new ArgumentException($"This operation requires exactly {expected} operands.");
    }

    public static void RequireCandidate(ExactValue? value, string type)
    {
        if (value is null || value.Type != type || value.Trust != ValueTrust.Candidate)
            throw new ArgumentException($"A typed {type} candidate is required.");
    }
}
