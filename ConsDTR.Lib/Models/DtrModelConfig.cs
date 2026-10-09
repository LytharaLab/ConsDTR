namespace ConsDTR.Lib.Models;

/// <summary>Shared neural templates; capacity and runtime budgets are independent of these weights.</summary>
public sealed record DtrModelConfig
{
    public string ArchitectureId { get; init; } = "ConsDTRv1";
    public string SpecRevision { get; init; } = "Draft-2";
    public string PolicySchemaVersion { get; init; } = "ConsDTRv1-HardPolicy-Draft-2/v1";
    public int HiddenDim { get; init; } = 128;
    public int RelationRank { get; init; } = 32;
    public int FeatureDim { get; init; } = 32;
    public int RelationTypes { get; init; } = 5;
    public int ScoreHiddenDim { get; init; } = 16;
    public float HiddenBound { get; init; } = 1;
    public int OperationClasses { get; init; } = 32;
    public int PortClasses { get; init; } = 16;
    public int MaximumSequenceLength { get; init; } = 128;
    public string ParameterDtype { get; init; } = "float32";

    public void Validate()
    {
        if (ArchitectureId != "ConsDTRv1" || SpecRevision != "Draft-2" || ParameterDtype != "float32" ||
            PolicySchemaVersion != "ConsDTRv1-HardPolicy-Draft-2/v1")
            throw new ArgumentException("Unsupported architecture, specification or parameter dtype.");
        if (HiddenDim < 1 || RelationRank < 1 || FeatureDim < 24 || RelationTypes < 1 ||
            ScoreHiddenDim < 1 || OperationClasses < 1 || OperationClasses > 128 ||
            PortClasses < 1 || PortClasses > 128 || MaximumSequenceLength < 1 ||
            !float.IsFinite(HiddenBound) || HiddenBound <= 0)
            throw new ArgumentException("Neural dimensions and bounds must be finite and positive.");
    }
}
