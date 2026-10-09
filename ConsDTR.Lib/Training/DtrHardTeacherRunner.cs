using ConsDTR.Lib.Inference;
using ConsDTR.Lib.Models;
using ConsDTR.Runtime;

namespace ConsDTR.Lib.Training;

/// <summary>
/// Offline hard BC uses exactly the production controller, directory, messages, receipts and gate.
/// A teacher can select only a member of the actual structural support; absent endpoints are not inserted.
/// </summary>
public sealed class DtrHardTeacherRunner(DtrNeuralModel model)
{
    public DtrHardRollout Collect(IKernelTask task,
        Func<DtrTeacherChoiceContext, DtrPolicyField, IReadOnlyList<int>, int?> teacher,
        DtrTrainingRecipe? recipe = null, DtrInferenceOptions? options = null, DtrRandomStream? random = null)
    {
        recipe ??= new DtrTrainingRecipe { CurriculumStage = "B-LocalRouting-TrainingScaffold" };
        recipe.Validate();
        options ??= new DtrInferenceOptions { Deterministic = false };
        if (options.Editor != DtrEditorMode.Learned || options.Messages != DtrMessageMode.Enabled ||
            options.Runtime.MaxMicrosteps < 1 || options.Runtime.MaxMicrosteps > recipe.MaximumEpisodeSteps ||
            task.Definition.Inputs.Count + (long)options.Runtime.WorkerCount + options.Runtime.RegisterCount + 1 > recipe.MaximumInputSlots)
            throw new ArgumentException("Teacher replay requires the real learned editor, messages and bounded recipe input/horizon.");
        options = options with { Deterministic = false, Runtime = options.Runtime with { Mode = ExecutionMode.OracleDiagnostic } };
        using var collector = new DtrHardRolloutCollector(model, recipe, random ?? new DtrRandomStream(options.SamplingSeed), options.Runtime.MaxWork, trainingScaffold: true);
        DtrTeacherChoiceContext? current = null;
        int? Choose(string name, DtrPolicyField field, IReadOnlyList<int> support)
        {
            if (current is null) throw new InvalidOperationException("A teacher field needs an actual production context: " + name);
            // Bit fields inherit the current candidate/variable metadata; their actual binary support is supplied below.
            var context = current.Name == name ? current : current with { Name = name };
            var selected = teacher(context, field, support);
            if (selected is { } choice && !support.Contains(choice))
                throw new ArgumentException("Teacher choice is outside the actual exposure/structural support: " + name);
            return selected;
        }
        var inference = new DtrInferenceRunner(model).RunCore(task, options, null, null, collector, Choose, context => current = context);
        return collector.Take(inference);
    }
}
