using ConsDTR.Lib.Inference;
using ConsDTR.Lib.Models;
using ConsDTR.Runtime;
using ConsDTR.Runtime.Discovery;
using ConsDTR.Runtime.Tasks;
using static TorchSharp.torch;

namespace ConsDTR.Lib.Training;

/// <summary>Course B BC over the real production controller and actual metered fair pages.
/// Teachers select existing support, wait for missing endpoints and never bypass receipts or the output gate.</summary>
public static class DtrHornTeacherExperiment
{
    public sealed record Result(double InitialLoss, double FinalLoss, int CertifiedTeacherEpisodes,
        int Updates, string Course = "B-LocalRouting-TrainingScaffold", string? CheckpointDigest = null,
        string? Directory = null, long OfflineTeacherFieldDecisions = 0);

    public static Result Run(string device = "cpu", int updates = 10, string? outputDirectory = null)
    {
        if (updates < 1) throw new ArgumentOutOfRangeException(nameof(updates));
        random.manual_seed(31);
        using var model = new DtrNeuralModel(new DtrModelConfig { HiddenDim = 32, RelationRank = 8 }, device);
        var recipe = new DtrTrainingRecipe
        {
            CurriculumStage = "B-LocalRouting-TrainingScaffold", TeacherVersion = "production-controller-one-rule-Horn/v1",
            LearningRate = 0.005, MaximumEpisodeSteps = 8, MaximumInputSlots = 8,
            SplitFingerprint = "one-rule-a-b-training-scaffold/v1"
        };
        using var trainer = new DtrTrainer(model, recipe);
        var randomStream = new DtrRandomStream(31);
        var task = new HornTask(new HornProblem(["a", "b"], ["a"], [new HornRule("r", ["a"], "b")], "b"));
        var options = new DtrInferenceOptions
        {
            Deterministic = false,
            Runtime = new SessionOptions
            {
                WorkerCount = 1, RegisterCount = 1, MaxSlots = 8, MaxMicrosteps = 8,
                MaxWork = 50_000_000, MaxControlReadsPerStep = 128, Mode = ExecutionMode.OracleDiagnostic
            },
            // Broad fair exposure is an explicit Course B scaffold; pages/probes/waiting still incur real costs.
            Directory = new DirectoryOptions
            {
                MaxTargetsPerStep = 8, FairTargetsPerStep = 8, CandidatesPerTarget = 8,
                StructuralQuota = 0, ExistingOrPinnedQuota = 0, FairQuota = 8
            }
        };
        double initial = 0, final = 0;
        var certified = 0;
        long teacherDecisions = 0;
        for (var update = 0; update < updates; update++)
        {
            var metrics = trainer.TrainBehaviorCloning(current =>
            {
                int? Teacher(DtrTeacherChoiceContext context, DtrPolicyField field, IReadOnlyList<int> support)
                {
                    teacherDecisions++;
                    return ChooseTeacher(context, field, support);
                }
                using var rollout = new DtrHardTeacherRunner(current).Collect(task, Teacher, recipe, options, randomStream);
                if (!rollout.Inference.IsVerified || rollout.Inference.Mode != ExecutionMode.OracleDiagnostic)
                {
                    throw new InvalidOperationException("Production teacher trajectory failed independent certification: " + rollout.Inference.Reason);
                }
                certified++;
                return rollout.TakePolicyTracesForBehaviorCloning();
            });
            if (update == 0) initial = metrics.Loss;
            final = metrics.Loss;
        }
        if (outputDirectory is not null) trainer.Save(outputDirectory, randomStream, recipe.SplitFingerprint);
        return new Result(initial, final, certified, updates, CheckpointDigest: DtrCheckpointStore.ComputeModelDigest(model),
            Directory: outputDirectory is null ? null : Path.GetFullPath(outputDirectory), OfflineTeacherFieldDecisions: teacherDecisions);
    }

    private static int ChooseTeacher(DtrTeacherChoiceContext context, DtrPolicyField field, IReadOnlyList<int> support)
    {
        var snapshot = context.Snapshot;
        var worker = snapshot.Slots.Single(slot => slot.Active && slot.Kind == SlotKind.Worker).Handle;
        var register = snapshot.Slots.Single(slot => slot.Active && slot.Kind == SlotKind.Register).Handle;
        var records = snapshot.Records.ToDictionary(record => record.RecordId);
        SlotHandle Input(string id) => snapshot.Slots.Single(slot => slot.RecordId is { } record && records[record].InputId == id).Handle;
        var rule = Input(HornTask.RuleInputId("r"));
        var fact = Input(HornTask.FactInputId("a"));
        var desired = new[]
        {
            new TopologyEdit(TopologyEditKind.AddEdge, rule, worker, EdgeRole.RuleInput, "rule"),
            new TopologyEdit(TopologyEditKind.AddEdge, fact, worker, EdgeRole.EvidenceInput, "p0"),
            new TopologyEdit(TopologyEditKind.AddEdge, worker, register, EdgeRole.WriteResult, "write")
        };
        bool Desired(TopologyEdit edit) => desired.Any(wanted => Same(edit, wanted));

        if (field == DtrPolicyField.EditKind)
        {
            var exposedRoute = context.EditChoices?.Any(Desired) == true;
            return exposedRoute && support.Contains((int)DtrEditKind.AddEdge) ?
                (int)DtrEditKind.AddEdge : (int)DtrEditKind.EndEdits;
        }
        if (context.EditChoices is { } edits)
        {
            foreach (var index in support)
                if (index < edits.Count && Desired(edits[index])) return index;
            throw new InvalidOperationException("TEACHER_EXPOSURE_MISS: desired route is absent from the actual production support.");
        }
        if (context.Name == "intent.worker" && context.SlotChoices is { } workers)
            return Index(workers, handle => handle == worker, support);
        if (context.Name == "intent.lifecycle") return support.Contains(0) ? 0 : support.Contains(1) ? 1 : support[0];
        if (context.Name == "intent.anchor" && context.SlotChoices is { } anchors)
        {
            var useful = support.FirstOrDefault(index => index < anchors.Count && (anchors[index] == rule || anchors[index] == fact), -1);
            return useful >= 0 ? useful : support[0];
        }
        if (context.Name == "action.operation" && context.Operations is { } operations)
            return Index(operations, operation => operation == HornTask.ApplyRule, support);
        if (context.Name == "action.operand-count") return support.Max();
        if (context.Name.StartsWith("action.operand", StringComparison.Ordinal) && context.ReadChoices is { } reads)
            return Index(reads, receipt => receipt.Source == rule || receipt.Source == fact, support);
        if (context.Name == "action.write-target" && context.WriteChoices is { } writes)
            return Index(writes, receipt => receipt.Target == register, support);
        if (context.Name == "action.stop")
            return desired.All(wanted => snapshot.Edges.Any(edge => Same(new TopologyEdit(TopologyEditKind.AddEdge,
                edge.Source, edge.Target, edge.Role, edge.Port), wanted))) ? 1 : 0;
        return support[0];
    }

    private static int Index<T>(IReadOnlyList<T> choices, Func<T, bool> predicate, IReadOnlyList<int> support)
    {
        foreach (var index in support) if (index < choices.Count && predicate(choices[index])) return index;
        throw new InvalidOperationException("TEACHER_EXPOSURE_MISS: required choice is not currently available.");
    }

    private static bool Same(TopologyEdit left, TopologyEdit right) => left.Source == right.Source &&
        left.Target == right.Target && left.Role == right.Role && left.Port == right.Port;
}
