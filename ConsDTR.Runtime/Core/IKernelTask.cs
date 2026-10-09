namespace ConsDTR.Runtime;

public interface IKernelTask
{
    TaskDefinition Definition { get; }
    KernelCheck Check(KernelAction action, KernelReadView view);
    ITaskVerifier CreateIndependentVerifier();
}

public interface ITaskVerifier
{
    SemanticVerification Verify(TaskDefinition input, IReadOnlyList<ProofEvent> proofs,
        ExactValue output, VerificationMeter meter);
}

public sealed record SemanticVerification(bool Accepted, string Reason, bool IsNegative = false);
