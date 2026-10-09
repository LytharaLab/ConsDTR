using ConsDTR.Runtime;

namespace ConsDTR.Lib.Models;

/// <summary>Offline teacher metadata for the actual bounded production support; never deployed to the policy.</summary>
public sealed record DtrTeacherChoiceContext(string Name, RuntimeSnapshot Snapshot,
    IReadOnlyList<SlotHandle>? SlotChoices = null, IReadOnlyList<TopologyEdit>? EditChoices = null,
    IReadOnlyList<ReadReceipt>? ReadChoices = null, IReadOnlyList<WriteReceipt>? WriteChoices = null,
    IReadOnlyList<string>? Operations = null);
