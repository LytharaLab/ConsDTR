using ConsDTR.Lib.Models;
using TorchSharp;
using static TorchSharp.torch;

namespace ConsDTR.Lib.Training;

/// <summary>
/// Runnable small hard-graph BC experiment. This is a labelled local routing scaffold, not an
/// autonomous task-capability evaluation or a replacement for runtime-verified teacher episodes.
/// </summary>
public static class DtrLocalTrainingExperiment
{
    public sealed record Result(double InitialLoss, double FinalLoss, double SparseReferenceError,
        double RemovedEdgeEffect, long ParameterCount, int Updates);

    public static Result Run(string device = "cpu", int updates = 20)
    {
        if (updates < 1) throw new ArgumentOutOfRangeException(nameof(updates));
        random.manual_seed(1);
        using var model = new DtrNeuralModel(new DtrModelConfig { HiddenDim = 32, RelationRank = 8 }, device);
        using var trainer = new DtrTrainer(model, new DtrTrainingRecipe { LearningRate = 0.005, MaximumEpisodeSteps = 8 });
        var stream = new DtrRandomStream(1);
        var policy = new DtrDiscretePolicy(model, stream);
        var featuresArray = new float[4 * model.Config.FeatureDim];
        featuresArray[0] = 1; featuresArray[4] = 1; featuresArray[8] = 1;
        featuresArray[model.Config.FeatureDim] = 1;
        featuresArray[2 * model.Config.FeatureDim + 2] = 1;
        featuresArray[3 * model.Config.FeatureDim + 1] = 1;
        var goalArray = new float[model.Config.FeatureDim]; goalArray[3] = 1;
        double initial = 0, final = 0;
        for (var update = 0; update < updates; update++)
        {
            var metrics = trainer.TrainBehaviorCloning(current =>
            {
                using var scope = NewDisposeScope();
                var trace = new DtrPolicyTrace();
                var z = current.EncodeLocal(tensor(featuresArray, device: current.Device).reshape(4, current.Config.FeatureDim));
                var hidden = current.InitializeHidden(z);
                var goal = current.EncodeGoal(tensor(goalArray, device: current.Device));
                var context = current.CreatePolicyContext(hidden, z, goal, 2, [1, 1, 1, 1]);
                void Field(DtrPolicyField field, string name, int teacher)
                {
                    var choice = policy.ChooseField(context, field, name, teacherChoice: teacher);
                    trace.Add(choice);
                    choice.LogProbability.MoveToOuterDisposeScope(); choice.Entropy.MoveToOuterDisposeScope();
                    context = current.AdvancePolicyContext(context, field, choice.Choice);
                }
                Field(DtrPolicyField.Intent, "intent", 0);
                Field(DtrPolicyField.EditKind, "edit0.kind", (int)DtrEditKind.AddEdge);
                var source = policy.Choose(current.PointerLogits(context, hidden, z, [0, 1, 2, 3]),
                    DtrPolicyField.Pointer, "edit0.source", teacherChoice: 0);
                trace.Add(source); source.LogProbability.MoveToOuterDisposeScope(); source.Entropy.MoveToOuterDisposeScope();
                context = current.AdvancePolicyContext(context, DtrPolicyField.Pointer, source.Choice);
                var target = policy.Choose(current.PointerLogits(context, hidden, z, [0, 1, 2, 3]),
                    DtrPolicyField.Pointer, "edit0.target", teacherChoice: 2);
                trace.Add(target); target.LogProbability.MoveToOuterDisposeScope(); target.Entropy.MoveToOuterDisposeScope();
                context = current.AdvancePolicyContext(context, DtrPolicyField.Pointer, target.Choice);
                Field(DtrPolicyField.Relation, "edit0.relation", 0);
                Field(DtrPolicyField.Port, "edit0.port", 0);
                // END_EDITS is an actual sampled teacher field, not an omitted forced early exit.
                Field(DtrPolicyField.EditKind, "edit1.kind", (int)DtrEditKind.EndEdits);
                hidden = current.ForwardAlongEdges(hidden, z, goal, [source.Choice], [target.Choice], [0]);
                context = current.CreatePolicyContext(hidden, z, goal, target.Choice, [1, 1, 1, 1]);
                Field(DtrPolicyField.Operation, "operation", 1);
                Field(DtrPolicyField.Stop, "stop", 0);
                return new[] { trace };
            });
            if (update == 0) initial = metrics.Loss;
            final = metrics.Loss;
        }
        using var diagnosticScope = NewDisposeScope();
        var local = model.EncodeLocal(tensor(featuresArray, device: model.Device).reshape(4, model.Config.FeatureDim));
        var prior = model.InitializeHidden(local);
        var encodedGoal = model.EncodeGoal(tensor(goalArray, device: model.Device));
        var sparse = model.ForwardAlongEdges(prior, local, encodedGoal, [0], [2], [0]);
        var reference = model.DenseReference(prior, local, encodedGoal, [0], [2], [0]);
        var removed = model.ForwardAlongEdges(prior, local, encodedGoal, [], [], []);
        return new Result(initial, final, (sparse - reference).abs().max().item<float>(),
            (sparse - removed).abs().max().item<float>(), model.ParameterCount, updates);
    }
}
