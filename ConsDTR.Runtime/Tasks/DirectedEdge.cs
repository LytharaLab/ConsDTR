namespace ConsDTR.Runtime.Tasks;

public sealed record DirectedEdge
{
    public string Id { get; }
    public string Source { get; }
    public string Target { get; }

    public DirectedEdge(string id, string source, string target)
    {
        Id = TaskIdentity.Require(id, nameof(id));
        Source = TaskIdentity.Require(source, nameof(source));
        Target = TaskIdentity.Require(target, nameof(target));
    }
}
