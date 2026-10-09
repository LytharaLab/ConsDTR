namespace ConsDTR.Lib.Training;

public static class DtrReward
{
    public static double Terminal(DtrTrainingRecipe recipe, bool independentlyVerified,
        long steps, long edits, long checks, long exactCost) =>
        recipe.CertificateReward * (independentlyVerified ? 1 : 0) -
        recipe.StepCostWeight * steps - recipe.EditCostWeight * edits -
        recipe.ExactCheckCostWeight * checks - recipe.ExactDataCostWeight * exactCost;

    /// <summary>All terminal states have potential zero, including UNKNOWN and numeric/resource failures.</summary>
    public static double PotentialDifference(double previousPotential, double nextPotential,
        bool terminal, double discount = 1)
    {
        if (!double.IsFinite(previousPotential) || !double.IsFinite(nextPotential) || discount is < 0 or > 1)
            throw new ArgumentException("Potential shaping requires finite fixed-state potentials and discount in [0,1].");
        return discount * (terminal ? 0 : nextPotential) - previousPotential;
    }
}
