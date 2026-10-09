using System.Text.Json;
using ConsDTR.Lib.Models;
using ConsDTR.Lib.Inference;
using ConsDTR.Runtime;
using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;

namespace ConsDTR.Lib.Training;

/// <summary>BC and reward-to-go REINFORCE; exact acceptance remains outside the autograd graph.</summary>
public sealed class DtrTrainer : IDisposable
{
    private readonly Adam _optimizer;
    public DtrNeuralModel Model { get; }
    public DtrTrainingRecipe Recipe { get; }
    public long CompletedUpdates { get; private set; }

    public DtrTrainer(DtrNeuralModel model, DtrTrainingRecipe? recipe = null)
    {
        Model = model;
        Recipe = recipe ?? new DtrTrainingRecipe();
        Recipe.Validate();
        model.BeginTraining();
        _optimizer = optim.Adam(model.parameters(), lr: Recipe.LearningRate);
    }

    /// <summary>The callback replays teacher fields over actual hard edges and actual candidate support.</summary>
    public DtrTrainingMetrics TrainBehaviorCloning(Func<DtrNeuralModel, IReadOnlyList<DtrPolicyTrace>> hardReplay)
    {
        using var scope = NewDisposeScope();
        Model.BeginTraining();
        _optimizer.zero_grad();
        var traces = hardReplay(Model);
        try
        {
            if (traces.Count == 0 || traces.Count > Recipe.MaximumEpisodeSteps)
                throw new ArgumentException("A BC episode must be nonempty and within the declared horizon.");
            if (traces.SelectMany(trace => trace.Choices).Any(choice => choice.ParameterVersion != Model.ParameterVersion))
                throw new ArgumentException("BC likelihoods must be freshly recomputed at the current parameter version.");
            var loss = -stack(traces.Select(trace => trace.JointLogProbability(Model.Device)).ToArray()).sum();
            var entropy = stack(traces.Select(trace => trace.JointEntropy(Model.Device)).ToArray()).sum();
            var number = loss.item<float>();
            ApplyLoss(loss);
            return new DtrTrainingMetrics(CompletedUpdates, number, number, 0, entropy.item<float>(),
                traces.Count, "HardBehaviorCloning", Recipe.TruncatedBpttSteps is null, Recipe.GradientNormClip is not null);
        }
        finally { foreach (var trace in traces) trace.Dispose(); }
    }

    /// <summary>No probability-ratio correction: this accepts only a freshly sampled current-policy episode.</summary>
    public DtrTrainingMetrics TrainOnPolicy(Func<DtrNeuralModel, IReadOnlyList<DtrRolloutStep>> hardRollout)
    {
        using var scope = NewDisposeScope();
        Model.BeginTraining();
        _optimizer.zero_grad();
        var steps = hardRollout(Model);
        try
        {
            if (steps.Count == 0 || steps.Count > Recipe.MaximumEpisodeSteps || !steps[^1].Terminal ||
                steps.Take(steps.Count - 1).Any(step => step.Terminal) || steps.Any(step => !double.IsFinite(step.Reward)))
                throw new ArgumentException("On-policy episodes need one final terminal, finite rewards, and a bounded horizon.");
            if (steps.SelectMany(step => step.Policy.Choices).Any(choice => choice.Origin != DtrChoiceOrigin.Sampled ||
                choice.ParameterVersion != Model.ParameterVersion))
                throw new ArgumentException("REINFORCE accepts current-policy samples; teacher/argmax/stale likelihoods require a different objective.");
            var policyLoss = zeros([], device: Model.Device);
            var valueLoss = zeros([], device: Model.Device);
            var entropy = zeros([], device: Model.Device);
            double rewardToGo = 0;
            for (var index = steps.Count - 1; index >= 0; index--)
            {
                rewardToGo += steps[index].Reward;
                var value = steps[index].ValueBeforeAction;
                DtrNeuralModel.EnsureFinite(value, "value baseline");
                var advantage = rewardToGo - value.detach();
                policyLoss = policyLoss - steps[index].Policy.JointLogProbability(Model.Device) * advantage;
                valueLoss = valueLoss + (value - rewardToGo).pow(2);
                entropy = entropy + steps[index].Policy.JointEntropy(Model.Device);
            }
            var loss = policyLoss + Recipe.ValueLossWeight * valueLoss - Recipe.EntropyWeight * entropy;
            var metrics = new DtrTrainingMetrics(CompletedUpdates + 1, loss.item<float>(), policyLoss.item<float>(),
                valueLoss.item<float>(), entropy.item<float>(), steps.Count, "OnPolicyRewardToGoREINFORCE",
                Recipe.TruncatedBpttSteps is null && steps.All(step => !step.BpttBoundary), Recipe.GradientNormClip is not null);
            ApplyLoss(loss);
            return metrics;
        }
        finally
        {
            foreach (var step in steps) { step.Policy.Dispose(); step.ValueBeforeAction.Dispose(); }
        }
    }

    /// <summary>Samples the production hard loop without scaffold, freezes no parameters, and retains full rollout records.</summary>
    public DtrTrainingMetrics TrainOnPolicy(IKernelTask task, DtrInferenceOptions? options = null,
        DtrRandomStream? random = null, Action<IReadOnlyList<DtrRolloutRecord>, DtrInferenceResult>? observer = null)
    {
        DtrHardRollout? rollout = null;
        try
        {
            var metrics = TrainOnPolicy(current =>
            {
                rollout = new DtrHardRolloutRunner(current).Collect(task, Recipe, options, random);
                return rollout.TakeStepsForTraining();
            });
            observer?.Invoke(rollout!.Records, rollout.Inference);
            return metrics;
        }
        finally { rollout?.Dispose(); }
    }

    public void Save(string directory, DtrRandomStream random, string splitFingerprint)
    {
        DtrCheckpointStore.Save(Model, directory, new DtrCheckpointManifest
        {
            TrainingRecipe = Recipe with { SplitFingerprint = splitFingerprint },
            DataSplitFingerprint = splitFingerprint, CompletedUpdates = CompletedUpdates
        });
        var optimizerPath = Path.Combine(directory, "optimizer.dat");
        _optimizer.save_state_dict(optimizerPath);
        File.WriteAllText(Path.Combine(directory, "training-state.json"), JsonSerializer.Serialize(new TrainingState(
            CompletedUpdates, random.State, random.DrawCount, DtrCheckpointStore.Digest(optimizerPath))));
    }

    public static (DtrTrainer Trainer, DtrRandomStream Random) Resume(string directory, string device = "cpu")
    {
        var (model, manifest) = DtrCheckpointStore.Load(directory, device, freezeForInference: false);
        DtrTrainer? trainer = null;
        try
        {
            var state = JsonSerializer.Deserialize<TrainingState>(File.ReadAllText(Path.Combine(directory, "training-state.json"))) ??
                throw new InvalidDataException("Missing resumable training state.");
            var optimizer = Path.Combine(directory, "optimizer.dat");
            if (state.Update != manifest.CompletedUpdates || state.OptimizerSha256 != DtrCheckpointStore.Digest(optimizer))
                throw new InvalidDataException("Optimizer/update state does not match the saved training checkpoint.");
            trainer = new DtrTrainer(model, manifest.TrainingRecipe) { CompletedUpdates = state.Update };
            model.ParameterVersion = state.Update;
            trainer._optimizer.load_state_dict(optimizer);
            return (trainer, new DtrRandomStream(state.RandomState, state.RandomDraws));
        }
        catch { trainer?.Dispose(); model.Dispose(); throw; }
    }

    private void ApplyLoss(Tensor loss)
    {
        DtrNeuralModel.EnsureFinite(loss, "training loss");
        loss.backward();
        foreach (var parameter in Model.parameters())
            if (parameter.grad is { } gradient) DtrNeuralModel.EnsureFinite(gradient, "parameter gradient");
        if (Recipe.GradientNormClip is { } clip) nn.utils.clip_grad_norm_(Model.parameters(), clip);
        _optimizer.step();
        foreach (var parameter in Model.parameters()) DtrNeuralModel.EnsureFinite(parameter, "updated parameter");
        CompletedUpdates++;
        Model.ParameterVersion++;
    }

    public void Dispose() => _optimizer.Dispose();

    private sealed record TrainingState(long Update, ulong RandomState, long RandomDraws, string OptimizerSha256);
}
