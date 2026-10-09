using System.Collections.Immutable;

namespace ConsDTR.Runtime.Tasks;

/// <summary>A finite, nonempty definite ground Horn clause. Repeated premises are normalized.</summary>
public sealed class HornRule
{
    public string Id { get; }
    public ImmutableArray<string> Premises { get; }
    public ImmutableArray<string> OriginalPremises { get; }
    public ImmutableArray<int> OriginalToCanonicalPremise { get; }
    public string Conclusion { get; }

    public HornRule(string id, IEnumerable<string> premises, string conclusion)
    {
        Id = TaskIdentity.Require(id, nameof(id));
        ArgumentNullException.ThrowIfNull(premises);
        OriginalPremises = premises.Select(value => TaskIdentity.Require(value, nameof(premises))).ToImmutableArray();
        Premises = TaskIdentity.Set(OriginalPremises, nameof(premises));
        if (Premises.IsEmpty)
            throw new ArgumentException("Facts must be input facts, not empty-premise rules.", nameof(premises));
        OriginalToCanonicalPremise = OriginalPremises.Select(value => Premises.IndexOf(value)).ToImmutableArray();
        Conclusion = TaskIdentity.Require(conclusion, nameof(conclusion));
    }

    internal Document ToDocument() => new(Id, OriginalPremises.ToArray(), Conclusion);
    internal static HornRule FromDocument(Document document) => new(document.Id, document.Premises, document.Conclusion);
    internal sealed record Document(string Id, string[] Premises, string Conclusion);
}
