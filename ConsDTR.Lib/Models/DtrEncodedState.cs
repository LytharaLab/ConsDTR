using ConsDTR.Runtime;
using TorchSharp;

namespace ConsDTR.Lib.Models;

public sealed class DtrEncodedState(torch.Tensor features, IReadOnlyList<SlotState> slots) : IDisposable
{
    public torch.Tensor Features { get; } = features;
    public IReadOnlyList<SlotState> Slots { get; } = slots;
    public IReadOnlyDictionary<long, int> SlotIndices { get; } =
        slots.Select((slot, index) => (slot.Handle.SlotId, index)).ToDictionary(pair => pair.SlotId, pair => pair.index);

    public void Dispose() => Features.Dispose();
}
