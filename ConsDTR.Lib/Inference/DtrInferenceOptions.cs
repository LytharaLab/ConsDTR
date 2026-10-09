using ConsDTR.Runtime;
using ConsDTR.Runtime.Discovery;

namespace ConsDTR.Lib.Inference;

public sealed record DtrInferenceOptions
{
    public SessionOptions Runtime { get; init; } = new();
    public DirectoryOptions Directory { get; init; } = new();
    public bool Deterministic { get; init; } = true;
    public ulong SamplingSeed { get; init; } = 1;
    public double Exploration { get; init; } = 0.05;
    public DtrEditorMode Editor { get; init; } = DtrEditorMode.Learned;
    public DtrMessageMode Messages { get; init; } = DtrMessageMode.Enabled;
}

public enum DtrEditorMode { Learned, Frozen, RandomLegal }
public enum DtrMessageMode { Enabled, Disabled, Shuffled }
