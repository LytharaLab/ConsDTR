namespace ConsDTR.Lib.Evaluation;

public sealed record DtrBootstrapOptions
{
    public int Seed { get; init; } = 89524;
    public int Resamples { get; init; } = 10_000;
    public long MaximumResampleDraws { get; init; } = 100_000_000;
    public string Algorithm => "PairedBinaryPercentile95/SystemRandom/v1";
    public void Validate()
    {
        if (Resamples < 100 || Resamples > 1_000_000 || MaximumResampleDraws < 1)
            throw new ArgumentException("Bootstrap requires a finite predeclared sample and work contract.");
    }
}
