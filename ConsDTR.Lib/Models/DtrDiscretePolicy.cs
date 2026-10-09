using TorchSharp;
using static TorchSharp.torch;

namespace ConsDTR.Lib.Models;

/// <summary>Hard autoregressive decoding with a complete differentiable conditional log likelihood.</summary>
public sealed class DtrDiscretePolicy(DtrNeuralModel model, DtrRandomStream? random = null,
    bool deterministic = false, double exploration = 0,
    Func<string, DtrPolicyField, IReadOnlyList<int>, int?>? teacherProvider = null)
{
    public DtrRandomStream Random { get; } = random ?? new DtrRandomStream();
    public int MaximumSequenceLength => model.Config.MaximumSequenceLength;
    public bool Deterministic { get; } = deterministic;
    public Func<string, DtrPolicyField, IReadOnlyList<int>, int?>? TeacherProvider { get; } = teacherProvider;
    public Tensor AdvanceContext(Tensor context, DtrPolicyField field, int choice) =>
        model.AdvancePolicyContext(context, field, choice);
    public double Exploration { get; } = exploration >= 0 && exploration <= 1 ? exploration :
        throw new ArgumentOutOfRangeException(nameof(exploration));

    public DtrPolicyChoice Choose(Tensor logits, DtrPolicyField field, string name,
        IReadOnlyList<int>? structuralSupport = null, int? teacherChoice = null)
    {
        using var scope = NewDisposeScope();
        DtrNeuralModel.EnsureFinite(logits, $"{name} logits");
        var flat = logits.reshape(-1);
        var support = structuralSupport?.ToArray() ?? Enumerable.Range(0, checked((int)flat.numel())).ToArray();
        if (support.Length == 0 || support.Distinct().Count() != support.Length ||
            support.Any(index => index < 0 || index >= flat.numel()))
            throw new ArgumentException("A categorical support must be nonempty, distinct, and structurally legal.");
        teacherChoice ??= TeacherProvider?.Invoke(name, field, support);
        var supportIndex = tensor(support.Select(index => (long)index).ToArray(), device: model.Device);
        var logProbabilities = flat.index_select(0, supportIndex).log_softmax(0);
        var probabilities = logProbabilities.exp();
        if (Exploration > 0)
        {
            probabilities = (1 - Exploration) * probabilities + Exploration / support.Length;
            logProbabilities = probabilities.log();
        }
        int position;
        if (teacherChoice is { } forced)
        {
            position = Array.IndexOf(support, forced);
            if (position < 0) throw new ArgumentException("Teacher choice is outside the actual structural support; exposure cannot be bypassed.");
        }
        else if (Deterministic)
            position = checked((int)probabilities.argmax().item<long>());
        else
        {
            var values = probabilities.cpu().data<float>().ToArray();
            var draw = Random.NextDouble();
            position = support.Length - 1;
            double cumulative = 0;
            for (var index = 0; index < values.Length; index++)
            {
                cumulative += values[index];
                if (draw < cumulative) { position = index; break; }
            }
        }
        var chosenLogProbability = logProbabilities.select(0, position);
        var entropy = -(probabilities * logProbabilities).sum();
        DtrNeuralModel.EnsureFinite(chosenLogProbability, $"{name} log probability");
        return new DtrPolicyChoice(name, field, support[position], support,
            chosenLogProbability.MoveToOuterDisposeScope(), entropy.MoveToOuterDisposeScope(),
            teacherChoice is not null ? DtrChoiceOrigin.Teacher : Deterministic ? DtrChoiceOrigin.DeterministicEvaluation :
            DtrChoiceOrigin.Sampled, model.ParameterVersion);
    }

    public DtrPolicyChoice ChooseField(Tensor context, DtrPolicyField field, string name,
        IReadOnlyList<int>? structuralSupport = null, int? teacherChoice = null)
    {
        using var scope = NewDisposeScope();
        var choice = Choose(model.FieldLogits(context, field), field, name, structuralSupport, teacherChoice);
        choice.LogProbability.MoveToOuterDisposeScope();
        choice.Entropy.MoveToOuterDisposeScope();
        return choice;
    }

    /// <summary>Signed checked-width candidate generation, not a 2^64-class head. Every bit is an action.</summary>
    public long DecodeInt64(Tensor initialContext, DtrPolicyTrace trace, long? teacherValue = null)
    {
        using var scope = NewDisposeScope();
        var context = initialContext;
        ulong bits = 0;
        for (var bit = 0; bit < 64; bit++)
        {
            var teacherBit = teacherValue is { } value ? (int)((unchecked((ulong)value) >> bit) & 1) : (int?)null;
            var choice = ChooseField(context, DtrPolicyField.Bit, $"integer.bit{bit}", teacherChoice: teacherBit);
            choice.LogProbability.MoveToOuterDisposeScope();
            choice.Entropy.MoveToOuterDisposeScope();
            trace.Add(choice);
            bits |= (ulong)choice.Choice << bit;
            context = model.AdvancePolicyContext(context, DtrPolicyField.Bit, choice.Choice);
        }
        return unchecked((long)bits);
    }

    /// <summary>Length and pointers are conditionally sampled; used for bounded sets and assignments.</summary>
    public int[] DecodeSequence(Tensor initialContext, Tensor hidden, Tensor local, int[] exposedSlots,
        DtrPolicyTrace trace, int maxLength, bool withoutReplacement = false, int[]? teacherValues = null,
        Action<string, int[]>? pointerSupport = null)
    {
        using var scope = NewDisposeScope();
        if (maxLength < 0 || maxLength > model.Config.MaximumSequenceLength ||
            exposedSlots.Distinct().Count() != exposedSlots.Length ||
            (teacherValues is not null && teacherValues.Length > maxLength))
            throw new ArgumentOutOfRangeException(nameof(maxLength));
        var limit = exposedSlots.Length == 0 ? 0 : withoutReplacement ? Math.Min(maxLength, exposedSlots.Length) : maxLength;
        var length = ChooseField(initialContext, DtrPolicyField.SequenceLength, "sequence.length",
            Enumerable.Range(0, limit + 1).ToArray(), teacherValues?.Length);
        length.LogProbability.MoveToOuterDisposeScope();
        length.Entropy.MoveToOuterDisposeScope();
        trace.Add(length);
        var context = model.AdvancePolicyContext(initialContext, DtrPolicyField.SequenceLength, length.Choice);
        var result = new int[length.Choice];
        var remaining = exposedSlots.ToList();
        for (var position = 0; position < result.Length; position++)
        {
            if (remaining.Count == 0) throw new ArgumentException("Cannot emit a pointer from an empty candidate support.");
            var forced = teacherValues is not null ? remaining.IndexOf(teacherValues[position]) : (int?)null;
            pointerSupport?.Invoke($"sequence.pointer{position}", remaining.ToArray());
            var choice = Choose(model.PointerLogits(context, hidden, local, remaining.ToArray()),
                DtrPolicyField.Pointer, $"sequence.pointer{position}", teacherChoice: forced);
            choice.LogProbability.MoveToOuterDisposeScope();
            choice.Entropy.MoveToOuterDisposeScope();
            trace.Add(choice);
            result[position] = remaining[choice.Choice];
            context = model.AdvancePolicyContext(context, DtrPolicyField.Pointer, choice.Choice);
            if (withoutReplacement) remaining.RemoveAt(choice.Choice);
        }
        return result;
    }
}
