using TorchSharp;
using static TorchSharp.torch;

namespace ConsDTR.Lib.Models;

public sealed class DtrPolicyTrace(bool retainTensors = false) : IDisposable
{
    private readonly List<DtrPolicyChoice> _choices = [];
    public IReadOnlyList<DtrPolicyChoice> Choices => _choices;
    public void Add(DtrPolicyChoice choice)
    {
        if (retainTensors)
        {
            choice.LogProbability.DetachFromDisposeScope();
            choice.Entropy.DetachFromDisposeScope();
        }
        _choices.Add(choice);
    }

    public Tensor JointLogProbability(Device device)
    {
        using var scope = NewDisposeScope();
        return (_choices.Count == 0 ? zeros([], device: device) :
            stack(_choices.Select(choice => choice.LogProbability).ToArray()).sum()).MoveToOuterDisposeScope();
    }

    public Tensor JointEntropy(Device device)
    {
        using var scope = NewDisposeScope();
        return (_choices.Count == 0 ? zeros([], device: device) :
            stack(_choices.Select(choice => choice.Entropy).ToArray()).sum()).MoveToOuterDisposeScope();
    }

    public void Dispose()
    {
        foreach (var choice in _choices) choice.Dispose();
        _choices.Clear();
    }
}
