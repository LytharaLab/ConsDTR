using System.Collections.Immutable;

namespace ConsDTR.Runtime.Tasks;

public sealed class CspVariable
{
    public string Id { get; }
    public ImmutableArray<int> Domain { get; }

    public CspVariable(string id, IEnumerable<int> domain)
    {
        Id = TaskIdentity.Require(id, nameof(id));
        ArgumentNullException.ThrowIfNull(domain);
        Domain = domain.Distinct().Order().ToImmutableArray();
        if (Domain.IsEmpty)
            throw new ArgumentException("A variable must have a nonempty finite domain.", nameof(domain));
    }

    internal Document ToDocument() => new(Id, Domain.ToArray());
    internal sealed record Document(string Id, int[] Domain);
}
