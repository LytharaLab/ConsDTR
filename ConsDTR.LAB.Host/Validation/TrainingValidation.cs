using ConsDTR.Lib.Inference;
using ConsDTR.Lib.Models;
using ConsDTR.Lib.Training;
using ConsDTR.Runtime;
using ConsDTR.Runtime.Discovery;
using ConsDTR.Runtime.Tasks;
using static TorchSharp.torch;

namespace ConsDTR.LAB.Host.Validation;

/// <summary>Production hard-rollout plumbing and accounting checks, without a learned task-success claim.</summary>
public static class TrainingValidation
{
    public static IReadOnlyList<string> Run(string device = "cpu")
    {
        var passed = new List<string>();
        random.manual_seed(927);
        var task = new HornTask(new HornProblem(["A", "B", "C"], ["A"],
            [new("ab", ["A"], "B"), new("bc", ["B"], "C")], "C"));
        var recipe = Recipe();
        var options = Options();
        using var model = CreateModel(device);
        var initialCheckpoint = DtrCheckpointStore.ComputeModelDigest(model);
        var stream = new DtrRandomStream(317);

        using (var rollout = new DtrHardRolloutRunner(model).Collect(task, recipe, options, stream))
        {
            Require(rollout.Steps.Count > 0 && rollout.Steps.Count <= recipe.MaximumEpisodeSteps
                && rollout.Steps.Count == rollout.Records.Count, "The production hard loop did not produce a bounded rollout.");
            Require(rollout.Inference.Journal is { Count: > 0 } prefix && prefix[0].EventKind == "GENESIS" &&
                prefix.Count == rollout.Inference.Snapshot.JournalEvents && prefix.Select((entry, index) => entry.EventSeq == index).All(valid => valid),
                "An UNKNOWN training rollout lost its complete diagnostic prefix or continuous event sequence.");
            Require(rollout.Steps.SelectMany(step => step.Policy.Choices).Any(), "No actual policy field was sampled.");
            Require(rollout.Steps.SelectMany(step => step.Policy.Choices).All(choice =>
                    choice.Origin == DtrChoiceOrigin.Sampled && choice.ParameterVersion == model.ParameterVersion),
                "Teacher, argmax or stale choices entered the on-policy rollout.");
            ValidateRecords(rollout, initialCheckpoint, options.Runtime.MaxWork);
            for (var stepIndex = 0; stepIndex < rollout.Steps.Count; stepIndex++)
            {
                var choices = rollout.Steps[stepIndex].Policy.Choices;
                var record = rollout.Records[stepIndex];
                Require(record.Fields.Count == choices.Count, "A conditional field likelihood disappeared from the record.");
                for (var fieldIndex = 0; fieldIndex < choices.Count; fieldIndex++)
                {
                    var choice = choices[fieldIndex];
                    var field = record.Fields[fieldIndex];
                    Require(field.Name == choice.Name && field.Field == (int)choice.Field && field.Choice == choice.Choice
                        && field.StructuralSupport.SequenceEqual(choice.Support)
                        && Math.Abs(field.ConditionalLogProbability - choice.LogProbability.item<float>()) < 1e-6,
                        "Recorded support or conditional probability differs from the live hard choice.");
                }
                Require(record.RandomDrawsBefore == rollout.Records.Take(stepIndex).Sum(previous => (long)previous.Fields.Count),
                    "Action RNG position does not follow the complete sampled field sequence.");
            }
            Require(stream.DrawCount == rollout.Records.Sum(record => (long)record.Fields.Count),
                "The live sampled action RNG used unrecorded draws.");
            Require(rollout.Steps[^1].Terminal && rollout.Steps.Take(rollout.Steps.Count - 1).All(step => !step.Terminal)
                && rollout.Records[^1].TerminationReason == rollout.Inference.Reason,
                "The hard rollout has a missing, repeated or incorrectly attributed terminal.");
            Require(DtrCheckpointStore.ComputeModelDigest(model) == initialCheckpoint,
                "Collecting a rollout updated policy parameters.");
        }
        passed.Add("Production LearnedOnly rollouts retain every sampled field, structural support, RNG position and terminal");
        passed.Add("Hard rollout records contain no teacher scaffold and exactly account for the complete runtime work ledger");

        using (var trainer = new DtrTrainer(model, recipe))
        {
            IReadOnlyList<DtrRolloutRecord>? observedRecords = null;
            DtrInferenceResult? observedInference = null;
            var version = model.ParameterVersion;
            var beforeUpdate = DtrCheckpointStore.ComputeModelDigest(model);
            var metrics = trainer.TrainOnPolicy(task, options, new DtrRandomStream(541), (records, inference) =>
            {
                observedRecords = records;
                observedInference = inference;
            });
            Require(observedRecords is { Count: > 0 } && observedInference is not null,
                "The task overload did not publish its actual hard-rollout records.");
            Require(observedRecords!.Sum(record => record.Cost) == options.Runtime.MaxWork - observedInference!.Snapshot.RemainingWork
                && observedRecords!.All(record => !record.TrainingScaffold && record.PolicyCheckpoint == beforeUpdate),
                "The task overload changed rollout provenance or omitted runtime cost.");
            Require(trainer.CompletedUpdates == 1 && metrics.Update == 1 && model.ParameterVersion == version + 1
                && DtrCheckpointStore.ComputeModelDigest(model) != beforeUpdate,
                "One production rollout did not produce exactly one actual parameter update.");
            Require(metrics.Objective == "OnPolicyRewardToGoREINFORCE" && metrics.FullBptt && !metrics.GradientClipped
                && new[] { metrics.Loss, metrics.PolicyLoss, metrics.ValueLoss, metrics.Entropy }.All(double.IsFinite),
                "The full-episode update lost finite losses or its declared gradient protocol.");
        }
        passed.Add("TrainOnPolicy(task) uses a fresh production episode and changes parameters once with finite reward-to-go losses");

        using (var idleModel = CreateModel(device))
        {
            var truncatedRecipe = recipe with { TruncatedBpttSteps = 1 };
            // One fair page prevents the discretionary worker target from being added. The first four pages
            // cover only observations/registers, exercising the runner's actual no-worker idle branch.
            var idleOptions = options with
            {
                Editor = DtrEditorMode.Frozen,
                Directory = new DirectoryOptions
                {
                    MaxTargetsPerStep = 1, CandidatesPerTarget = 4, StructuralQuota = 0,
                    ExistingOrPinnedQuota = 0, FairQuota = 4, FairTargetsPerStep = 1
                }
            };
            using (var idle = new DtrHardRolloutRunner(idleModel).Collect(task, truncatedRecipe, idleOptions, new DtrRandomStream(631)))
            {
                Require(idle.Steps.Count == 4 && idle.Steps.Any(step => step.Policy.Choices.Count == 0)
                    && idle.Inference.AcceptedEdits == 0 && idle.Inference.Snapshot.Edges.Count == 0,
                    "The declared idle fixture did not traverse the no-worker branch without routing edits.");
                Require(idle.Steps.All(step => step.BpttBoundary) && idle.Records.All(record => record.BpttBoundary),
                    "A configured one-step BPTT boundary was omitted on an actual idle step.");
                ValidateRecords(idle, DtrCheckpointStore.ComputeModelDigest(idleModel), idleOptions.Runtime.MaxWork);
            }
            using var truncatedTrainer = new DtrTrainer(idleModel, truncatedRecipe);
            var metrics = truncatedTrainer.TrainOnPolicy(task, idleOptions, new DtrRandomStream(641));
            Require(!metrics.FullBptt && !truncatedRecipe.UsesFullUnbiasedScoreGradient
                && double.IsFinite(metrics.Loss) && truncatedTrainer.CompletedUpdates == 1,
                "An idle truncated update was mislabeled as the complete unbiased gradient protocol.");
        }
        passed.Add("Actual no-worker idle steps retain TBPTT boundaries and truncated updates report FullBptt=false");

        using (var failureModel = CreateModel(device))
        {
            // GENESIS succeeds, while the first inference audit cannot append another event.
            var failureOptions = options with { Runtime = options.Runtime with { MaxJournalEvents = 1 } };
            using (var failure = new DtrHardRolloutRunner(failureModel).Collect(task, recipe, failureOptions, new DtrRandomStream(719)))
            {
                Require(failure.Inference.Status == DtrResultStatus.ResourceOrNumericFailure
                    && failure.Inference.Snapshot.MicrostepId == -1 && failure.Steps.Count == 0 && failure.Records.Count == 0
                    && failure.Inference.Reason.Contains("journal", StringComparison.OrdinalIgnoreCase),
                    "An initialization resource failure was hidden behind an invented or thrown training episode.");
            }
            using var failureTrainer = new DtrTrainer(failureModel, recipe);
            var checkpoint = DtrCheckpointStore.ComputeModelDigest(failureModel);
            var version = failureModel.ParameterVersion;
            ExpectArgument(() => failureTrainer.TrainOnPolicy(task, failureOptions, new DtrRandomStream(727)),
                "The trainer accepted an episode with no sampled training step.");
            Require(failureTrainer.CompletedUpdates == 0 && failureModel.ParameterVersion == version
                && DtrCheckpointStore.ComputeModelDigest(failureModel) == checkpoint,
                "Rejecting an empty failure episode still changed policy parameters.");
        }
        passed.Add("A failure before STEP_BEGIN remains a resource result with zero steps and cannot update the trainer");

        using (var frozenModel = CreateModel(device))
        {
            frozenModel.FreezeForInference();
            ExpectInvalidOperation(frozenModel.BeginTraining, "A frozen inference checkpoint entered training.");
            ExpectInvalidOperation(() => new DtrHardRolloutRunner(frozenModel).Collect(task, recipe, options).Dispose(),
                "The production training loop accepted a frozen inference model.");
        }
        passed.Add("Frozen inference parameters cannot enter training or produce a mutable on-policy rollout");
        return passed.AsReadOnly();
    }

    private static DtrNeuralModel CreateModel(string device) =>
        new(new DtrModelConfig { HiddenDim = 16, RelationRank = 4 }, device);

    private static DtrTrainingRecipe Recipe() => new()
    {
        MaximumEpisodeSteps = 4, MaximumInputSlots = 64, EntropyWeight = 0.01,
        CurriculumStage = "C-AutonomousJoint", TeacherVersion = "none/production-hard-loop",
        SplitFingerprint = "training-validation-fixtures/v1"
    };

    private static DtrInferenceOptions Options() => new()
    {
        Deterministic = false, SamplingSeed = 317, Exploration = 0.05,
        Runtime = new SessionOptions
        {
            Mode = ExecutionMode.LearnedOnly, WorkerCount = 2, RegisterCount = 16, MaxSlots = 64,
            MaxMicrosteps = 4, MaxWork = 100_000_000, FinalGateWorkAllowance = 2_000_000
        }
    };

    private static void ValidateRecords(DtrHardRollout rollout, string expectedCheckpoint, long maximumWork)
    {
        Require(rollout.Inference.Mode == ExecutionMode.LearnedOnly
            && rollout.Records.All(record => !record.TrainingScaffold && record.PolicyCheckpoint == expectedCheckpoint),
            "A teacher/fallback or unrelated checkpoint entered the autonomous record.");
        Require(rollout.Records.Sum(record => record.Cost) == maximumWork - rollout.Inference.Snapshot.RemainingWork
            && rollout.Records.All(record => record.Cost >= 0 && double.IsFinite(record.Reward)),
            "Step and terminal costs do not equal the complete runtime debit.");
        var slots = rollout.Inference.Snapshot.Slots.Select(slot => slot.Handle.SlotId).ToHashSet();
        Require(rollout.Records.All(record => record.CandidateOrder.Distinct().Count() == record.CandidateOrder.Count
                && record.CandidateOrder.All(slots.Contains)
                && record.Fields.All(field => field.StructuralSupport.Length > 0
                    && field.StructuralSupport.Distinct().Count() == field.StructuralSupport.Length
                    && field.StructuralSupport.Contains(field.Choice) && double.IsFinite(field.ConditionalLogProbability))),
            "Recorded candidate identity, sampled support or conditional likelihood is malformed.");
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException("Training validation failed: " + reason);
    }

    private static void ExpectArgument(Action action, string reason)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Training validation failed: " + reason);
    }

    private static void ExpectInvalidOperation(Action action, string reason)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Training validation failed: " + reason);
    }
}
