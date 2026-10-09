namespace ConsDTR.Runtime;

internal sealed record TopologyPayload(TopologyTransaction Transaction, bool Accepted,
    bool Changed, string Reason, IReadOnlyList<DataEdge> AddedEdges);
