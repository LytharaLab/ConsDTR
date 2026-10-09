using System.Collections.Immutable;

namespace ConsDTR.Runtime.Tasks;

/// <summary>Exact, explicit finite-domain operation semantics; no unknown function or floating comparison.</summary>
public sealed class FiniteConstraint
{
    public string Id { get; }
    public ConstraintKind Kind { get; }
    public ImmutableArray<string> Variables { get; }
    public ImmutableArray<BooleanLiteral> Literals { get; }
    public ImmutableArray<ImmutableArray<int>> AllowedTuples { get; }

    private FiniteConstraint(string id, ConstraintKind kind, IEnumerable<string> variables,
        IEnumerable<BooleanLiteral>? literals = null, IEnumerable<IEnumerable<int>>? allowedTuples = null)
    {
        Id = TaskIdentity.Require(id, nameof(id));
        Kind = kind;
        Variables = variables.Select(value => TaskIdentity.Require(value, nameof(variables))).ToImmutableArray();
        Literals = (literals ?? []).ToImmutableArray();
        AllowedTuples = (allowedTuples ?? []).Select(tuple => tuple.ToImmutableArray()).ToImmutableArray();
        if ((Variables.IsEmpty && kind != ConstraintKind.BooleanClause) || (kind is ConstraintKind.Equal or ConstraintKind.NotEqual && Variables.Length != 2) ||
            AllowedTuples.Any(tuple => tuple.Length != Variables.Length))
            throw new ArgumentException("Constraint arity is invalid.");
    }

    public static FiniteConstraint Equal(string id, string left, string right) => new(id, ConstraintKind.Equal, [left, right]);
    public static FiniteConstraint NotEqual(string id, string left, string right) => new(id, ConstraintKind.NotEqual, [left, right]);
    public static FiniteConstraint Clause(string id, IEnumerable<BooleanLiteral> literals)
    {
        var values = literals.ToArray();
        return new(id, ConstraintKind.BooleanClause, values.Select(literal => literal.Variable), values);
    }
    public static FiniteConstraint Table(string id, IEnumerable<string> variables, IEnumerable<IEnumerable<int>> tuples) =>
        new(id, ConstraintKind.AllowedTable, variables, allowedTuples: tuples);

    internal Document ToDocument() => new(Id, Kind, Variables.ToArray(), Literals.ToArray(), AllowedTuples.Select(tuple => tuple.ToArray()).ToArray());
    internal static FiniteConstraint FromDocument(Document value) => value.Kind switch
    {
        ConstraintKind.Equal => Equal(value.Id, value.Variables[0], value.Variables[1]),
        ConstraintKind.NotEqual => NotEqual(value.Id, value.Variables[0], value.Variables[1]),
        ConstraintKind.BooleanClause => Clause(value.Id, value.Literals),
        ConstraintKind.AllowedTable => Table(value.Id, value.Variables, value.AllowedTuples),
        _ => throw new ArgumentException("Unknown CSP operation code.")
    };

    public readonly record struct BooleanLiteral(string Variable, bool Negated = false);
    internal sealed record Document(string Id, ConstraintKind Kind, string[] Variables, BooleanLiteral[] Literals, int[][] AllowedTuples);
    public enum ConstraintKind { Equal, NotEqual, BooleanClause, AllowedTable }
}
