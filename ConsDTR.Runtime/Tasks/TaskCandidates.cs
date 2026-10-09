namespace ConsDTR.Runtime.Tasks;

/// <summary>Candidate constructors never certify a proposition. Revisions bind complete immutable serialized values.</summary>
public static class TaskCandidates
{
    public static ExactValue HornClosedSet(HornProblem problem, IEnumerable<string> members, long revision = 1) =>
        Set("horn.set", problem.Fingerprint, members, revision);

    public static ExactValue GraphCut(GraphPathProblem problem, IEnumerable<string> members, long revision = 1) =>
        Set("path.cut", problem.Fingerprint, members, revision);

    /// <summary>A page of members targeting the next revision, rather than a closed-set certificate.</summary>
    public static ExactValue HornClosedSetDelta(HornProblem problem, IEnumerable<string> members, long targetRevision) =>
        Set("horn.set-delta", problem.Fingerprint, members, targetRevision);

    /// <summary>A page of vertices targeting the next cut revision; membership remains unverified.</summary>
    public static ExactValue GraphCutDelta(GraphPathProblem problem, IEnumerable<string> members, long targetRevision) =>
        Set("path.cut-delta", problem.Fingerprint, members, targetRevision);

    public static ExactValue AssignmentDelta(CspProblem problem, IEnumerable<KeyValuePair<string, int>> values, long targetRevision) =>
        AssignmentValueOf("csp.assignment-delta", problem, values, targetRevision);

    public static ExactValue Assignment(CspProblem problem, IEnumerable<KeyValuePair<string, int>> values, long revision = 1) =>
        AssignmentValueOf("csp.assignment", problem, values, revision);

    /// <summary>Reads only the serialized candidate header; callers must use a currently delivered value.</summary>
    public static long RevisionOf(ExactValue candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Trust != ValueTrust.Candidate)
            throw new ArgumentException("A candidate revision cannot be read from a verified fact.", nameof(candidate));
        var revision = candidate.Type switch
        {
            "horn.set" or "horn.set-delta" or "path.cut" or "path.cut-delta" => TaskJson.Read<SetValue>(candidate.Data).Revision,
            "csp.assignment" or "csp.assignment-delta" => TaskJson.Read<AssignmentValue>(candidate.Data).Revision,
            _ => throw new ArgumentException("Unsupported candidate type.", nameof(candidate))
        };
        if (revision < 1) throw new ArgumentException("Candidate revisions must be positive.", nameof(candidate));
        return revision;
    }

    private static ExactValue AssignmentValueOf(string type, CspProblem problem, IEnumerable<KeyValuePair<string, int>> values, long revision)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(values);
        if (revision < 1)
            throw new ArgumentOutOfRangeException(nameof(revision));
        var entries = values.Select(entry => new AssignmentEntry(TaskIdentity.Require(entry.Key, nameof(values)), entry.Value))
            .OrderBy(entry => entry.Variable, StringComparer.Ordinal).ToArray();
        TaskIdentity.RequireDistinct(entries.Select(entry => entry.Variable), nameof(values));
        return new ExactValue(type, TaskJson.Write(new AssignmentValue(problem.Fingerprint, revision, entries)), ValueTrust.Candidate);
    }

    private static ExactValue Set(string type, string fingerprint, IEnumerable<string> members, long revision)
    {
        if (revision < 1)
            throw new ArgumentOutOfRangeException(nameof(revision));
        return new ExactValue(type, TaskJson.Write(new SetValue(fingerprint, revision, TaskIdentity.Set(members, nameof(members)).ToArray())), ValueTrust.Candidate);
    }

    internal sealed record SetValue(string Fingerprint, long Revision, string[] Members);
    internal sealed record AssignmentEntry(string Variable, int Value);
    internal sealed record AssignmentValue(string Fingerprint, long Revision, AssignmentEntry[] Entries);
}
