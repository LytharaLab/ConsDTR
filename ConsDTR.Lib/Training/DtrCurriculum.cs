namespace ConsDTR.Lib.Training;

public enum DtrCurriculumStage { LocalOperation, LocalRouting, AutonomousJoint, FailureRecovery, OnPolicy, FrozenEvaluation }

/// <summary>Predeclared gates keep scaffold and fallback success separate from autonomous ability.</summary>
public sealed class DtrCurriculum
{
    public DtrCurriculumStage Stage { get; private set; } = DtrCurriculumStage.LocalOperation;
    public double RequiredLocalSuccess { get; }
    public int MaximumUnimprovedChecks { get; }
    public bool ExpansionStopped { get; private set; }
    public int UnimprovedChecks { get; private set; }
    private double _bestAutonomousRoute;

    public DtrCurriculum(double requiredLocalSuccess = 0.95, int maximumUnimprovedChecks = 10)
    {
        if (requiredLocalSuccess is < 0 or > 1 || maximumUnimprovedChecks < 1)
            throw new ArgumentOutOfRangeException(nameof(requiredLocalSuccess));
        RequiredLocalSuccess = requiredLocalSuccess;
        MaximumUnimprovedChecks = maximumUnimprovedChecks;
    }

    public void Advance(double localOperationSuccess, double localRouteSuccess, double autonomousRouteSuccess,
        bool protocolTestsPassed, bool capabilityAcceptancePassed = false)
    {
        if (new[] { localOperationSuccess, localRouteSuccess, autonomousRouteSuccess }.Any(value => !double.IsFinite(value) || value is < 0 or > 1))
            throw new ArgumentException("Curriculum metrics are finite rates in [0,1].");
        if (!protocolTestsPassed) throw new InvalidOperationException("P0 protocol/teacher acceptance must pass before neural curriculum expansion.");
        switch (Stage)
        {
            case DtrCurriculumStage.LocalOperation when localOperationSuccess >= RequiredLocalSuccess:
                Stage = DtrCurriculumStage.LocalRouting; break;
            case DtrCurriculumStage.LocalRouting when localRouteSuccess >= RequiredLocalSuccess:
                Stage = DtrCurriculumStage.AutonomousJoint; break;
            case DtrCurriculumStage.AutonomousJoint:
            case DtrCurriculumStage.FailureRecovery:
                if (autonomousRouteSuccess > _bestAutonomousRoute) { _bestAutonomousRoute = autonomousRouteSuccess; UnimprovedChecks = 0; }
                else UnimprovedChecks++;
                ExpansionStopped = autonomousRouteSuccess < RequiredLocalSuccess && UnimprovedChecks >= MaximumUnimprovedChecks;
                Stage = autonomousRouteSuccess >= RequiredLocalSuccess ? DtrCurriculumStage.OnPolicy : DtrCurriculumStage.FailureRecovery;
                break;
            case DtrCurriculumStage.OnPolicy when capabilityAcceptancePassed:
                Stage = DtrCurriculumStage.FrozenEvaluation; break;
        }
    }
}
