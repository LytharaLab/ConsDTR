using ConsDTR.Lib.Models;
using ConsDTR.Runtime;
using ConsDTR.Runtime.Discovery;
using TorchSharp;

namespace ConsDTR.Lib.Inference;

/// <summary>Borrowed completed-step view. Return true from the callback to stop at an exclusive resume cut.</summary>
public sealed record DtrInferenceCheckpointContext(RuntimeSession Runtime, DtrNeuralModel Model,
    torch.Tensor Hidden, IReadOnlyList<SlotState> NeuralSlots, CandidateDirectory Directory,
    IntentState Intent, DtrRandomStream ActionRandom, DtrRandomStream EditorRandom, DtrInferenceOptions Options)
{
    public DtrSessionArchive Save(string path) => DtrSessionArchive.Save(this, path);
}
