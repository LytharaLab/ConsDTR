using ConsDTR.Runtime;

namespace ConsDTR.Lib.Evaluation;

/// <summary>Oracle labels are evaluation metadata; the inference runner receives only Task.</summary>
public sealed record DtrEvaluationCase(IKernelTask Task, string Split, string Family, int Scale,
    int StructureSeed, bool? IsSatisfiable = null);
