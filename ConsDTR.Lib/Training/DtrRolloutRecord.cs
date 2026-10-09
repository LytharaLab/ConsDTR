namespace ConsDTR.Lib.Training;

/// <summary>Training replay metadata is distinct from the trusted runtime certificate.</summary>
public sealed record DtrRolloutRecord(string PolicyCheckpoint, ulong RandomStateBefore, long RandomDrawsBefore,
    long MicrostepId, long TopologyRevision, long ExactStateRevision,
    IReadOnlyList<DtrFieldRecord> Fields, IReadOnlyList<long> CandidateOrder,
    bool Accepted, double Reward, long Cost, string TerminationReason, bool BpttBoundary,
    bool TrainingScaffold, DtrFailureAttribution? OfflineFailureAttribution = null);

public sealed record DtrFieldRecord(string Name, int Field, int Choice, int[] StructuralSupport, double ConditionalLogProbability);

public enum DtrFailureAttribution
{
    ExposureMiss, ExposedButUnchosen, ChosenButNotRouted, ReceiptReadyButWrongOperation,
    ValidProgressThenStall, PrematureStop
}
