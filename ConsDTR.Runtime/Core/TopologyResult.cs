namespace ConsDTR.Runtime;

public sealed record TopologyResult(bool Accepted, bool Changed, string Reason, long TopologyRevision, IReadOnlyList<DataEdge> AddedEdges);
