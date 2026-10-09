using System.Collections.Immutable;

namespace ConsDTR.Runtime.Tasks;

/// <summary>Immutable finite ground Horn input. A negative answer means NOT_DERIVABLE.</summary>
public sealed class HornProblem
{
    public ImmutableArray<string> Atoms { get; }
    public ImmutableArray<string> Facts { get; }
    public ImmutableArray<HornRule> Rules { get; }
    public string Query { get; }
    public string CanonicalInput { get; }
    public string CanonicalQuery { get; }
    public string Fingerprint { get; }

    public HornProblem(IEnumerable<string> atoms, IEnumerable<string> facts, IEnumerable<HornRule> rules, string query)
    {
        Atoms = TaskIdentity.Set(atoms, nameof(atoms));
        Facts = TaskIdentity.Set(facts, nameof(facts));
        ArgumentNullException.ThrowIfNull(rules);
        Rules = rules.OrderBy(rule => rule.Id, StringComparer.Ordinal).ToImmutableArray();
        TaskIdentity.RequireDistinct(Rules.Select(rule => rule.Id), nameof(rules));
        Query = TaskIdentity.Require(query, nameof(query));
        var universe = Atoms.ToHashSet(StringComparer.Ordinal);
        if (!universe.Contains(Query) || Facts.Any(fact => !universe.Contains(fact)) ||
            Rules.Any(rule => !universe.Contains(rule.Conclusion) || rule.Premises.Any(p => !universe.Contains(p))))
            throw new ArgumentException("Every fact, query and rule atom must belong to the declared finite universe.");
        CanonicalInput = TaskJson.Write(new Document(Atoms.ToArray(), Facts.ToArray(), Rules.Select(rule => rule.ToDocument()).ToArray()));
        CanonicalQuery = TaskJson.Write(Query);
        Fingerprint = TaskJson.Fingerprint(CanonicalInput, CanonicalQuery);
    }

    internal static HornProblem Parse(string input, string query)
    {
        var document = TaskJson.Read<Document>(input);
        return new HornProblem(document.Atoms, document.Facts, document.Rules.Select(HornRule.FromDocument), TaskJson.Read<string>(query));
    }

    internal sealed record Document(string[] Atoms, string[] Facts, HornRule.Document[] Rules);
}
