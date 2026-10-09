using ConsDTR.Lib.Inference;

namespace ConsDTR.Lib.Training;

/// <summary>Live, current-policy likelihoods and an independent audit record for one actual hard-runtime episode.</summary>
public sealed class DtrHardRollout(IReadOnlyList<DtrRolloutStep> steps, IReadOnlyList<DtrRolloutRecord> records,
    DtrInferenceResult inference) : IDisposable
{
    private bool released;
    public IReadOnlyList<DtrRolloutStep> Steps { get; } = steps;
    public IReadOnlyList<DtrRolloutRecord> Records { get; } = records;
    public DtrInferenceResult Inference { get; } = inference;

    /// <summary>Transfers all live tensor ownership to the trainer/caller. Dispose no longer releases them.</summary>
    public IReadOnlyList<DtrRolloutStep> TakeStepsForTraining()
    {
        if (released) throw new InvalidOperationException("Rollout tensor ownership has already been released.");
        released = true;
        return Steps;
    }

    /// <summary>Releases unused critic tensors and transfers likelihood trace ownership to a BC trainer.</summary>
    public IReadOnlyList<ConsDTR.Lib.Models.DtrPolicyTrace> TakePolicyTracesForBehaviorCloning()
    {
        if (released) throw new InvalidOperationException("Rollout tensor ownership has already been released.");
        foreach (var step in Steps) step.ValueBeforeAction.Dispose();
        released = true;
        return Steps.Select(step => step.Policy).ToArray();
    }

    public void Dispose()
    {
        if (released) return;
        released = true;
        foreach (var step in Steps) { step.Policy.Dispose(); step.ValueBeforeAction.Dispose(); }
    }
}
