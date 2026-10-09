using ConsDTR.Runtime;

namespace ConsDTR.Lib.Evaluation;

/// <summary>Wilson binomial intervals and deterministic paired percentile bootstrap.</summary>
public static class DtrBinaryStatistics
{
    public static DtrConfidenceInterval Wilson95(int successes, int observations)
    {
        if (observations < 0 || successes < 0 || successes > observations) throw new ArgumentOutOfRangeException(nameof(successes));
        if (observations == 0) return new(0, 1, 0.95, "Wilson95/NoObservations", 0);
        const double z = 1.959963984540054;
        var zSquared = z * z; var p = (double)successes / observations;
        var denominator = 1 + zSquared / observations;
        var center = (p + zSquared / (2 * observations)) / denominator;
        var radius = z * Math.Sqrt(p * (1 - p) / observations + zSquared / (4 * observations * (double)observations)) / denominator;
        return new(successes == 0 ? 0 : Math.Max(0, center - radius), successes == observations ? 1 : Math.Min(1, center + radius),
            0.95, "WilsonScore95/v1", observations);
    }

    public static DtrPairedEstimate PairedBootstrap95(IReadOnlyList<int> left, IReadOnlyList<int> right,
        DtrBootstrapOptions? options = null)
    {
        options ??= new(); options.Validate();
        if (left.Count == 0 || left.Count != right.Count || left.Any(x => x is not (0 or 1)) || right.Any(x => x is not (0 or 1)))
            throw new ArgumentException("Paired certified indicators must have matching nonempty binary observations.");
        if ((long)left.Count * options.Resamples > options.MaximumResampleDraws)
            throw new RuntimeResourceException("Paired bootstrap exceeds its predeclared draw allowance.");
        var differences = left.Zip(right, (a, b) => a - b).ToArray();
        var random = new Random(options.Seed); var samples = new double[options.Resamples];
        for (var sample = 0; sample < samples.Length; sample++)
        {
            long total = 0;
            for (var draw = 0; draw < differences.Length; draw++) total += differences[random.Next(differences.Length)];
            samples[sample] = (double)total / differences.Length;
        }
        Array.Sort(samples);
        static double Quantile(double[] sorted, double probability)
        {
            var position = (sorted.Length - 1) * probability; var lower = (int)Math.Floor(position); var upper = (int)Math.Ceiling(position);
            return sorted[lower] + (position - lower) * (sorted[upper] - sorted[lower]);
        }
        return new(differences.Average(), new(Quantile(samples, 0.025), Quantile(samples, 0.975), 0.95,
            "PairedPercentileBootstrap95/v1", differences.Length), differences.Length, options.Seed,
            options.Resamples, options.Algorithm);
    }
}
