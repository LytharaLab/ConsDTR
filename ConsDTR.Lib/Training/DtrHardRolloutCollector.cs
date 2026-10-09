using ConsDTR.Lib.Inference;
using ConsDTR.Lib.Models;
using ConsDTR.Runtime;
using ConsDTR.Runtime.Discovery;
using TorchSharp;

namespace ConsDTR.Lib.Training;

/// <summary>Training tensors never become semantic premises or part of the formal certificate.</summary>
internal sealed class DtrHardRolloutCollector(DtrNeuralModel model, DtrTrainingRecipe recipe,
    DtrRandomStream random, long maximumWork, bool trainingScaffold = false) : IDisposable
{
    private readonly List<DtrRolloutStep> steps = [];
    private readonly List<DtrRolloutRecord> records = [];
    private readonly string checkpoint = DtrCheckpointStore.ComputeModelDigest(model);
    private RuntimeSnapshot? before;
    private DtrPolicyTrace? pending;
    private torch.Tensor? value;
    private ulong stateBefore;
    private long drawsBefore;
    private long accountedWork;
    private long accountedEdits;
    private bool accepted;
    private int checks;
    private long edits;
    private long[] candidates = [];
    private bool transferred;

    public DtrRandomStream Random { get; } = random;

    public DtrPolicyTrace BeginStep(RuntimeSnapshot snapshot)
    {
        if (pending is not null) throw new InvalidOperationException("An unfinished training microstep cannot be replaced.");
        before = snapshot; stateBefore = Random.State; drawsBefore = Random.DrawCount;
        accepted = false; checks = 0; edits = 0; candidates = [];
        return pending = new DtrPolicyTrace(retainTensors: true);
    }

    public void SetCandidates(IReadOnlyList<CandidateHeader> headers) => candidates = headers.Select(h => (long)h.Slot).ToArray();
    public void SetCommit(bool wasAccepted) { checks++; accepted = wasAccepted; }
    public void SetEdits(long count) => edits = count;

    public void SetValue(torch.Tensor baseline)
    {
        value?.Dispose();
        value = baseline.DetachFromDisposeScope();
    }

    public bool IsBpttBoundary(long microstep) => recipe.TruncatedBpttSteps is { } interval && (microstep + 1) % interval == 0;

    public void EndStep(RuntimeSnapshot snapshot, bool bpttBoundary = false)
    {
        if (pending is null || before is null) return;
        var work = checked(maximumWork - snapshot.RemainingWork - accountedWork);
        // The terminal ledger also includes initialization/final-gate allowances. It is a deterministic work cost, not FLOPs.
        var reward = -recipe.StepCostWeight - recipe.EditCostWeight * edits - recipe.ExactCheckCostWeight * checks
            - recipe.ExactDataCostWeight * work;
        value ??= torch.zeros([], device: model.Device).DetachFromDisposeScope();
        steps.Add(new(pending, value, reward, false, before.ExactStateRevision, bpttBoundary));
        records.Add(new(checkpoint, stateBefore, drawsBefore, before.MicrostepId, before.TopologyRevision,
            before.ExactStateRevision, pending.Choices.Select(choice => new DtrFieldRecord(choice.Name, (int)choice.Field,
                choice.Choice, choice.Support.ToArray(), choice.LogProbability.item<float>())).ToArray(), candidates,
            accepted, reward, work, "CONTINUE", bpttBoundary, TrainingScaffold: trainingScaffold));
        accountedWork += work; accountedEdits += edits;
        pending = null; value = null; before = null;
    }

    public void Finish(DtrInferenceResult result)
    {
        EndStep(result.Snapshot);
        if (steps.Count == 0) return; // No policy action exists before a failed initialization; the trainer cannot update from this result.
        var totalWork = checked(maximumWork - result.Snapshot.RemainingWork);
        var terminalCost = checked(totalWork - accountedWork);
        var terminalEdits = checked(result.AcceptedEdits - accountedEdits);
        var bonus = recipe.CertificateReward * (result.IsVerified && result.Verification?.Accepted == true && result.Certificate is not null ? 1 : 0)
            - recipe.ExactDataCostWeight * terminalCost - recipe.EditCostWeight * terminalEdits;
        steps[^1] = steps[^1] with { Terminal = true, Reward = steps[^1].Reward + bonus };
        records[^1] = records[^1] with { Reward = records[^1].Reward + bonus, Cost = records[^1].Cost + terminalCost,
            TerminationReason = result.Reason };
        accountedWork = totalWork;
    }

    public DtrHardRollout Take(DtrInferenceResult inference)
    {
        transferred = true;
        return new(Array.AsReadOnly(steps.ToArray()), Array.AsReadOnly(records.ToArray()), inference);
    }

    public void Dispose()
    {
        pending?.Dispose(); value?.Dispose();
        if (!transferred) foreach (var step in steps) { step.Policy.Dispose(); step.ValueBeforeAction.Dispose(); }
    }
}
