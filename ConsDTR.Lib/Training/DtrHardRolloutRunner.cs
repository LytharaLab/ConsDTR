using ConsDTR.Lib.Inference;
using ConsDTR.Lib.Models;
using ConsDTR.Runtime;

namespace ConsDTR.Lib.Training;

/// <summary>Course C/D rollouts use the same directory, hard editor, inbox, kernel and gate as frozen inference.</summary>
public sealed class DtrHardRolloutRunner(DtrNeuralModel model)
{
    public DtrHardRollout Collect(IKernelTask task, DtrTrainingRecipe? recipe = null,
        DtrInferenceOptions? options = null, DtrRandomStream? random = null)
    {
        recipe ??= new DtrTrainingRecipe { CurriculumStage = "C-AutonomousJoint" };
        recipe.Validate();
        options ??= new DtrInferenceOptions { Deterministic = false };
        if (options.Deterministic || options.Runtime.Mode != ExecutionMode.LearnedOnly ||
            options.Runtime.MaxMicrosteps < 1 || options.Runtime.MaxMicrosteps > recipe.MaximumEpisodeSteps ||
            task.Definition.Inputs.Count + (long)options.Runtime.WorkerCount + options.Runtime.RegisterCount + 1 > recipe.MaximumInputSlots)
            throw new ArgumentException("Hard on-policy training requires sampled LearnedOnly actions and the recipe's bounded input/horizon.");
        using var collector = new DtrHardRolloutCollector(model, recipe, random ?? new DtrRandomStream(options.SamplingSeed), options.Runtime.MaxWork);
        var inference = new DtrInferenceRunner(model).RunCore(task, options, null, null, collector);
        return collector.Take(inference);
    }
}
