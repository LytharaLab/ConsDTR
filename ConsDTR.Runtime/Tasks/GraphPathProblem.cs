using System.Collections.Immutable;

namespace ConsDTR.Runtime.Tasks;

public sealed class GraphPathProblem
{
    public ImmutableArray<string> Vertices { get; }
    public ImmutableArray<DirectedEdge> Edges { get; }
    public string Source { get; }
    public string Target { get; }
    public bool RequireSimplePath { get; }
    public string CanonicalInput { get; }
    public string CanonicalQuery { get; }
    public string Fingerprint { get; }

    public GraphPathProblem(IEnumerable<string> vertices, IEnumerable<DirectedEdge> edges, string source, string target,
        bool requireSimplePath = false)
    {
        Vertices = TaskIdentity.Set(vertices, nameof(vertices));
        ArgumentNullException.ThrowIfNull(edges);
        Edges = edges.OrderBy(edge => edge.Id, StringComparer.Ordinal).ToImmutableArray();
        TaskIdentity.RequireDistinct(Edges.Select(edge => edge.Id), nameof(edges));
        Source = TaskIdentity.Require(source, nameof(source));
        Target = TaskIdentity.Require(target, nameof(target));
        RequireSimplePath = requireSimplePath;
        var universe = Vertices.ToHashSet(StringComparer.Ordinal);
        if (!universe.Contains(Source) || !universe.Contains(Target) ||
            Edges.Any(edge => !universe.Contains(edge.Source) || !universe.Contains(edge.Target)))
            throw new ArgumentException("All edge endpoints and query vertices must belong to the declared graph.");
        CanonicalInput = TaskJson.Write(new Document(Vertices.ToArray(), Edges.ToArray()));
        CanonicalQuery = TaskJson.Write(new QueryDocument(Source, Target, RequireSimplePath));
        Fingerprint = TaskJson.Fingerprint(CanonicalInput, CanonicalQuery);
    }

    internal static GraphPathProblem Parse(string input, string query)
    {
        var document = TaskJson.Read<Document>(input);
        var goal = TaskJson.Read<QueryDocument>(query);
        return new GraphPathProblem(document.Vertices, document.Edges, goal.Source, goal.Target, goal.RequireSimplePath);
    }

    internal sealed record Document(string[] Vertices, DirectedEdge[] Edges);
    internal sealed record QueryDocument(string Source, string Target, bool RequireSimplePath);
}
