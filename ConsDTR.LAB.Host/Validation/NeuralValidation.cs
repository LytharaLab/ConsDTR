using ConsDTR.Lib.Models;
using ConsDTR.Lib.Training;
using ConsDTR.Runtime;
using System.Text.Json.Nodes;
using System.Text.Json;
using TorchSharp;
using static TorchSharp.torch;

namespace ConsDTR.LAB.Host.Validation;

/// <summary>Finite implementation checks; these do not establish autonomous task coverage.</summary>
public static class NeuralValidation
{
    public static IReadOnlyList<string> Run(string device = "cpu")
    {
        var passed = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Neural validation failed: " + name);
            passed.Add(name);
        }

        var training = DtrLocalTrainingExperiment.Run(device, 20);
        Check(training.FinalLoss < training.InitialLoss, $"hard BC loss decreases ({training.InitialLoss:F3} -> {training.FinalLoss:F3})");
        Check(training.SparseReferenceError < 1e-5, "actual hard-edge gather/scatter equals DenseReference");
        Check(training.RemovedEdgeEffect > 1e-7, "deleting a hard edge changes neural messages");

        using var model = new DtrNeuralModel(new DtrModelConfig { HiddenDim = 16, RelationRank = 4 }, device);
        using var trainer = new DtrTrainer(model, new DtrTrainingRecipe { MaximumEpisodeSteps = 128, EntropyWeight = 0 });
        var stream = new DtrRandomStream(123);
        var policy = new DtrDiscretePolicy(model, stream, exploration: 0.1);
        var features = Features(model);
        var goalFeatures = new float[model.Config.FeatureDim]; goalFeatures[3] = 1;

        using (var scope = NewDisposeScope())
        {
            var local = model.EncodeLocal(tensor(features, device: model.Device).reshape(3, model.Config.FeatureDim));
            var hidden = model.InitializeHidden(local);
            var goal = model.EncodeGoal(tensor(goalFeatures, device: model.Device));
            for (var cycle = 0; cycle < 100; cycle++)
                hidden = model.ForwardAlongEdges(hidden, local, goal, [0, 1, 2], [1, 2, 0], [0, 4, 4]);
            Check(hidden.abs().max().item<float>() <= model.Config.HiddenBound + 1e-5, "100 recurrent cycles preserve the convex hidden bound");
            var inactive = model.ForwardAlongEdges(hidden, local, goal, [0], [1], [0], [true, false, true]);
            Check((inactive.select(0, 1) - hidden.select(0, 1)).abs().max().item<float>() == 0, "inactive slots retain their state");
            var previousSlots = Enumerable.Range(0, 3).Select(index => new SlotState(new SlotHandle(index, 0), SlotKind.Worker, true)).ToArray();
            var resetSlots = previousSlots.ToArray(); resetSlots[0] = resetSlots[0] with { Handle = new SlotHandle(0, 1) };
            using var resetEncoding = new DtrEncodedState(zeros(3, model.Config.FeatureDim, device: model.Device), resetSlots);
            var synchronized = DtrFeatureEncoder.SynchronizeHidden(model, previousSlots, hidden, resetEncoding, local);
            Check(synchronized.select(0, 0).abs().max().item<float>() == 0 &&
                (synchronized.select(0, 1) - hidden.select(0, 1)).abs().max().item<float>() == 0,
                "reset clears the changed generation while preserving live identities");
            var context = model.CreatePolicyContext(hidden, local, goal, 1, [1, 1, 1, 1]);
            using var bitTrace = new DtrPolicyTrace();
            const long integer = long.MinValue + 345;
            Check(policy.DecodeInt64(context, bitTrace, integer) == integer && bitTrace.Choices.Count == 64,
                "signed 64-bit candidate has all 64 conditional likelihoods");
            using var trace = new DtrPolicyTrace();
            var decoded = policy.DecodeSequence(context, hidden, local, [0, 1, 2], trace, 3,
                withoutReplacement: true, teacherValues: [2, 0]);
            Check(decoded.SequenceEqual(new[] { 2, 0 }) && trace.Choices.Count == 3,
                "bounded sequence records length and conditional without-replacement pointers");
            using var emptyTrace = new DtrPolicyTrace();
            Check(policy.DecodeSequence(context, hidden, local, [], emptyTrace, 3, teacherValues: []).Length == 0 &&
                emptyTrace.Choices.Count == 1, "empty pointer support permits only the explicit zero-length sequence");
            var rejected = false;
            try { policy.Choose(tensor(new[] { float.NaN, 0f }, device: model.Device), DtrPolicyField.Bit, "nan").Dispose(); }
            catch (DtrNumericException) { rejected = true; }
            Check(rejected, "NaN logits reject before categorical masking or sampling");
            rejected = false;
            try { model.ForwardAlongEdges(full([3, model.Config.HiddenDim], double.PositiveInfinity, device: model.Device), local, goal, [], [], []); }
            catch (DtrNumericException) { rejected = true; }
            Check(rejected, "nonfinite hidden states cannot continue a trajectory");
        }

        _ = stream.NextDouble();
        var restoredStream = new DtrRandomStream(stream.State, stream.DrawCount);
        Check(stream.NextDouble() == restoredStream.NextDouble(), "saved RNG state restores the next hard-action draw");
        trainer.TrainBehaviorCloning(current => Teacher(current, features, goalFeatures));
        var directory = Path.Combine(Environment.CurrentDirectory, ".artifacts", "neural-validation", Guid.NewGuid().ToString("N"));
        trainer.Save(directory, stream, "finite-smoke-fixture/v1");
        var digest = DtrCheckpointStore.ComputeModelDigest(model);
        var (restored, manifest) = DtrCheckpointStore.Load(directory, device);
        using (restored)
        {
            Check(restored.IsFrozen && restored.parameters().All(parameter => !parameter.requires_grad), "loaded inference parameters are frozen");
            Check(digest == DtrCheckpointStore.ComputeModelDigest(restored), "weights and configuration survive checkpoint serialization");
            var rejected = false;
            try { restored.BeginTraining(); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "inference model cannot enter a training session");
        }

        var malformedDirectory = Path.Combine(directory, "negative-load");
        Directory.CreateDirectory(malformedDirectory);
        var missingMetadata = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")))!.AsObject();
        missingMetadata.Remove("KernelSemanticVersion");
        File.WriteAllText(Path.Combine(malformedDirectory, "manifest.json"), missingMetadata.ToJsonString());
        var loadRejected = false;
        try { DtrCheckpointStore.Load(malformedDirectory, device).Model.Dispose(); }
        catch (InvalidDataException) { loadRejected = true; }
        Check(loadRejected, "missing mandatory manifest metadata cannot use defaults");
        var manifestText = File.ReadAllText(Path.Combine(directory, "manifest.json"));
        File.WriteAllText(Path.Combine(malformedDirectory, "manifest.json"), "{\"CheckpointId\":\"duplicate\"," + manifestText[1..]);
        loadRejected = false;
        try { DtrCheckpointStore.Load(malformedDirectory, device).Model.Dispose(); }
        catch (InvalidDataException) { loadRejected = true; }
        Check(loadRejected, "duplicate checkpoint manifest fields are rejected");
        var unknownMetadata = JsonNode.Parse(manifestText)!.AsObject();
        unknownMetadata["UnknownSemanticField"] = "unsupported";
        File.WriteAllText(Path.Combine(malformedDirectory, "manifest.json"), unknownMetadata.ToJsonString());
        loadRejected = false;
        try { DtrCheckpointStore.Load(malformedDirectory, device).Model.Dispose(); }
        catch (JsonException error) when (error.Message.Contains("UnknownSemanticField", StringComparison.Ordinal)) { loadRejected = true; }
        Check(loadRejected, "unknown semantic manifest fields cannot be silently ignored");
        File.WriteAllText(Path.Combine(malformedDirectory, "manifest.json"), manifestText);
        var corruptedWeights = File.ReadAllBytes(Path.Combine(directory, "weights.dat"));
        corruptedWeights[^1] ^= 1;
        File.WriteAllBytes(Path.Combine(malformedDirectory, "weights.dat"), corruptedWeights);
        loadRejected = false;
        try { DtrCheckpointStore.Load(malformedDirectory, device).Model.Dispose(); }
        catch (InvalidDataException) { loadRejected = true; }
        Check(loadRejected, "changed neural checkpoint bytes fail the archived weight digest");

        var (resumed, resumedRandom) = DtrTrainer.Resume(directory, device);
        using (resumed)
        using (resumed.Model)
        {
            Check(resumedRandom.State == stream.State && resumed.CompletedUpdates == manifest.CompletedUpdates,
                "training checkpoint restores RNG and completed updates");
            trainer.TrainBehaviorCloning(current => Teacher(current, features, goalFeatures));
            resumed.TrainBehaviorCloning(current => Teacher(current, features, goalFeatures));
            Check(DtrCheckpointStore.ComputeModelDigest(model) == DtrCheckpointStore.ComputeModelDigest(resumed.Model),
                "Adam moments resume the same next parameter update");
        }

        var reinforce = trainer.TrainOnPolicy(current =>
        {
            using var scope = NewDisposeScope();
            var local = current.EncodeLocal(tensor(features, device: current.Device).reshape(3, current.Config.FeatureDim));
            var hidden = current.InitializeHidden(local);
            var goal = current.EncodeGoal(tensor(goalFeatures, device: current.Device));
            hidden = current.ForwardAlongEdges(hidden, local, goal, [0], [1], [0]);
            var context = current.CreatePolicyContext(hidden, local, goal, 1, [1, 1, 1, 1]);
            var baseline = current.Value(context); // Taken before the field it baselines.
            var trace = new DtrPolicyTrace();
            var choice = policy.ChooseField(context, DtrPolicyField.Operation, "sampled-operation");
            trace.Add(choice); choice.LogProbability.MoveToOuterDisposeScope(); choice.Entropy.MoveToOuterDisposeScope();
            return new[] { new DtrRolloutStep(trace, baseline.MoveToOuterDisposeScope(), choice.Choice == 1 ? 1 : -0.1, true, 0) };
        });
        Check(double.IsFinite(reinforce.Loss) && reinforce.FullBptt, "on-policy hard-action REINFORCE and detached value baseline run");
        var teacherRejected = false;
        try
        {
            trainer.TrainOnPolicy(current => new[]
            {
                new DtrRolloutStep(Teacher(current, features, goalFeatures)[0], zeros([], device: current.Device), 1, true, 0)
            });
        }
        catch (ArgumentException) { teacherRejected = true; }
        Check(teacherRejected, "teacher-forced likelihoods cannot be relabelled as on-policy samples");
        return passed;
    }

    private static float[] Features(DtrNeuralModel model)
    {
        var values = new float[3 * model.Config.FeatureDim];
        values[0] = 1; values[4] = 1; values[8] = 1;
        values[model.Config.FeatureDim + 2] = 1;
        values[2 * model.Config.FeatureDim + 1] = 1;
        return values;
    }

    private static IReadOnlyList<DtrPolicyTrace> Teacher(DtrNeuralModel current, float[] features, float[] goalFeatures)
    {
        using var scope = NewDisposeScope();
        var local = current.EncodeLocal(tensor(features, device: current.Device).reshape(3, current.Config.FeatureDim));
        var hidden = current.InitializeHidden(local);
        var goal = current.EncodeGoal(tensor(goalFeatures, device: current.Device));
        hidden = current.ForwardAlongEdges(hidden, local, goal, [0], [1], [0]);
        var context = current.CreatePolicyContext(hidden, local, goal, 1, [1, 1, 1, 1]);
        var policy = new DtrDiscretePolicy(current);
        var trace = new DtrPolicyTrace();
        var choice = policy.ChooseField(context, DtrPolicyField.Operation, "teacher-operation", teacherChoice: 1);
        trace.Add(choice); choice.LogProbability.MoveToOuterDisposeScope(); choice.Entropy.MoveToOuterDisposeScope();
        return new[] { trace };
    }
}
