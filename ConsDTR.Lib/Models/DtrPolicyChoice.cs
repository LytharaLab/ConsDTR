using TorchSharp;

namespace ConsDTR.Lib.Models;

public sealed record DtrPolicyChoice(string Name, DtrPolicyField Field, int Choice, int[] Support,
    torch.Tensor LogProbability, torch.Tensor Entropy, DtrChoiceOrigin Origin = DtrChoiceOrigin.Sampled,
    long ParameterVersion = 0) : IDisposable
{
    public void Dispose()
    {
        LogProbability.Dispose();
        Entropy.Dispose();
    }
}

public enum DtrChoiceOrigin { Sampled, Teacher, DeterministicEvaluation }
