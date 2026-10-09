using System.Text.Json;
using ConsDTR.Lib.Inference;
using ConsDTR.Lib.Models;
using ConsDTR.Runtime;
using ConsDTR.Runtime.Tasks;
using static TorchSharp.torch;

namespace ConsDTR.Lib.Training;

/// <summary>Small autonomous Course C training example. Outcomes are measured, never inferred from a teacher.</summary>
public static class DtrOnPolicyExperiment
{
    public sealed record Result(int Updates, int CertifiedEpisodes, string CheckpointDigest, string Directory,
        IReadOnlyList<DtrTrainingMetrics> Metrics, string Course = "C-AutonomousJoint");

    public static Result Run(string device = "cpu", int updates = 4, string? outputDirectory = null)
    {
        if (updates < 1) throw new ArgumentOutOfRangeException(nameof(updates));
        var output = Path.GetFullPath(outputDirectory ?? Path.Combine(Environment.CurrentDirectory, "run", "onpolicy"));
        Directory.CreateDirectory(output);
        random.manual_seed(43);
        using var model = new DtrNeuralModel(new DtrModelConfig { HiddenDim = 32, RelationRank = 8 }, device);
        const string split = "short-Horn-chains/autonomous-training-only/v1";
        var recipe = new DtrTrainingRecipe
        {
            CurriculumStage = "C-AutonomousJoint", TeacherVersion = "none",
            Seed = 43, MaximumEpisodeSteps = 8, MaximumInputSlots = 64, SplitFingerprint = split
        };
        using var trainer = new DtrTrainer(model, recipe);
        var stream = new DtrRandomStream(43);
        var metrics = new List<DtrTrainingMetrics>();
        var certified = 0;
        for (var update = 0; update < updates; update++)
        {
            var symbols = Enumerable.Range(0, 4).Select(i => $"s{update}_{i}").ToArray();
            var task = new HornTask(new HornProblem(symbols, [symbols[0]],
                [new($"r{update}_0", [symbols[0]], symbols[1]), new($"r{update}_1", [symbols[1]], symbols[2])],
                update % 2 == 0 ? symbols[2] : symbols[3]));
            var options = new DtrInferenceOptions
            {
                Deterministic = false, Exploration = 0.05, SamplingSeed = 43,
                Runtime = new SessionOptions { WorkerCount = 2, RegisterCount = 6, MaxSlots = 64,
                    MaxMicrosteps = recipe.MaximumEpisodeSteps, MaxWork = 100_000_000, FinalGateWorkAllowance = 1_000_000 }
            };
            // Archive the behavior policy before its update, so the saved hard likelihood tape can be recomputed.
            var behaviorPolicy = Path.Combine(output, $"policy-{update:D4}");
            trainer.Save(behaviorPolicy, stream, split);
            metrics.Add(trainer.TrainOnPolicy(task, options, stream, (records, result) =>
            {
                if (result.IsVerified) certified++;
                File.WriteAllText(Path.Combine(output, $"episode-{update:D4}.json"), JsonSerializer.Serialize(new
                {
                    Status = result.Status.ToString(), result.Reason, Mode = result.Mode.ToString(), Records = records,
                    Task = task.Definition, Options = options, BehaviorPolicy = behaviorPolicy, RuntimeJournal = result.Journal,
                    TrainingScaffold = false, CapabilityValidated = false
                }, new JsonSerializerOptions { WriteIndented = true }));
            }));
        }
        trainer.Save(Path.Combine(output, "checkpoint"), stream, split);
        return new(updates, certified, DtrCheckpointStore.ComputeModelDigest(model), output, metrics.AsReadOnly());
    }
}
