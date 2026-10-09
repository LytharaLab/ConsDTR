using ConsDTR.Lib.Models;
using ConsDTR.Runtime;
using ConsDTR.Runtime.Discovery;
using TorchSharp;

namespace ConsDTR.Lib.Inference;

/// <summary>Owned exclusive resume workspace; Runner.Run consumes its runtime lifetime.</summary>
public sealed class DtrInferenceResume(RuntimeSession runtime, torch.Tensor hidden,
    IReadOnlyList<SlotState> neuralSlots, CandidateDirectory directory, IntentState intent,
    DtrRandomStream actionRandom, DtrRandomStream editorRandom, DtrInferenceOptions options) : IDisposable
{
    public RuntimeSession Runtime { get; } = runtime;
    public torch.Tensor Hidden { get; } = hidden;
    public IReadOnlyList<SlotState> NeuralSlots { get; } = neuralSlots;
    public CandidateDirectory Directory { get; } = directory;
    public IntentState Intent { get; } = intent;
    public DtrRandomStream ActionRandom { get; } = actionRandom;
    public DtrRandomStream EditorRandom { get; } = editorRandom;
    public DtrInferenceOptions Options { get; } = options;
    public void Dispose() { Hidden.Dispose(); Runtime.Dispose(); }
}
