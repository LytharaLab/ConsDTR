namespace ConsDTR.Runtime;

internal sealed record ProbePayload(long ControlViewId, IReadOnlyList<SlotHandle> Handles);
