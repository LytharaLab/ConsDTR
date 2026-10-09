namespace ConsDTR.Runtime.Tasks;

/// <summary>Explicit persistent progress. Every NEXT consumes the previous accumulator and its bound candidate.</summary>
internal sealed record TaskCheckAccumulator(string Kind, string Fingerprint, long CandidateRevision,
    ExactValue Candidate, string[] ExpectedInputIds, string[] CheckedInputIds)
{
    public static TaskCheckAccumulator Begin(string kind, string fingerprint, long revision, ExactValue candidate, IEnumerable<string> expected) =>
        new(kind, fingerprint, revision, candidate, expected.Order(StringComparer.Ordinal).ToArray(), []);

    public TaskCheckAccumulator Next(string inputId)
    {
        if (!ExpectedInputIds.Contains(inputId, StringComparer.Ordinal) || CheckedInputIds.Contains(inputId, StringComparer.Ordinal))
            throw new ArgumentException("The check item is undeclared or already covered.");
        return this with { CheckedInputIds = CheckedInputIds.Append(inputId).Order(StringComparer.Ordinal).ToArray() };
    }

    public bool Matches(string kind, string fingerprint, IEnumerable<string> expected, ExactValue? candidate = null) =>
        Kind == kind && Fingerprint == fingerprint && CandidateRevision >= 1 && Candidate.Trust == ValueTrust.Candidate &&
        ExpectedInputIds.SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal) &&
        CheckedInputIds.Distinct(StringComparer.Ordinal).Count() == CheckedInputIds.Length &&
        CheckedInputIds.All(id => ExpectedInputIds.Contains(id, StringComparer.Ordinal)) &&
        (candidate is null || Candidate == candidate);

    public bool Complete => ExpectedInputIds.SequenceEqual(CheckedInputIds, StringComparer.Ordinal);
}
