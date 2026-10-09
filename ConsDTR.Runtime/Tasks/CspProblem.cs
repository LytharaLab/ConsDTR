using System.Collections.Immutable;

namespace ConsDTR.Runtime.Tasks;

/// <summary>Finite-domain witness task. v1 proves SATISFIES and never claims UNSAT.</summary>
public sealed class CspProblem
{
    public ImmutableArray<CspVariable> Variables { get; }
    public ImmutableArray<FiniteConstraint> Constraints { get; }
    public string CanonicalInput { get; }
    public string CanonicalQuery => "\"SATISFIES\"";
    public string Fingerprint { get; }

    public CspProblem(IEnumerable<CspVariable> variables, IEnumerable<FiniteConstraint> constraints)
    {
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(constraints);
        Variables = variables.OrderBy(variable => variable.Id, StringComparer.Ordinal).ToImmutableArray();
        Constraints = constraints.OrderBy(constraint => constraint.Id, StringComparer.Ordinal).ToImmutableArray();
        TaskIdentity.RequireDistinct(Variables.Select(variable => variable.Id), nameof(variables));
        TaskIdentity.RequireDistinct(Constraints.Select(constraint => constraint.Id), nameof(constraints));
        var byId = Variables.ToDictionary(variable => variable.Id, StringComparer.Ordinal);
        foreach (var constraint in Constraints)
        {
            if (constraint.Variables.Any(variable => !byId.ContainsKey(variable)))
                throw new ArgumentException("A constraint names an undeclared variable.", nameof(constraints));
            if (constraint.Kind == FiniteConstraint.ConstraintKind.BooleanClause &&
                constraint.Variables.Any(variable => byId[variable].Domain.Any(value => value is not (0 or 1))))
                throw new ArgumentException("Boolean clauses require domains contained in {0,1}.", nameof(constraints));
            if (constraint.AllowedTuples.Any(tuple => tuple.Where((value, index) => !byId[constraint.Variables[index]].Domain.Contains(value)).Any()))
                throw new ArgumentException("A table tuple contains an out-of-domain value.", nameof(constraints));
        }
        CanonicalInput = TaskJson.Write(new Document(Variables.Select(variable => variable.ToDocument()).ToArray(), Constraints.Select(constraint => constraint.ToDocument()).ToArray()));
        Fingerprint = TaskJson.Fingerprint(CanonicalInput, CanonicalQuery);
    }

    internal static CspProblem Parse(string input, string query)
    {
        if (query != "\"SATISFIES\"")
            throw new ArgumentException("Unsupported CSP output predicate.", nameof(query));
        var document = TaskJson.Read<Document>(input);
        return new CspProblem(document.Variables.Select(variable => new CspVariable(variable.Id, variable.Domain)), document.Constraints.Select(FiniteConstraint.FromDocument));
    }

    internal sealed record Document(CspVariable.Document[] Variables, FiniteConstraint.Document[] Constraints);
}
