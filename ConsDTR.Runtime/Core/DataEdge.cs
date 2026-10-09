namespace ConsDTR.Runtime;

public enum EdgeRole { EvidenceInput, RuleInput, CandidateValue, WriteResult, NeuralMessage }

public sealed record DataEdge(long EdgeId, SlotHandle Source, SlotHandle Target,
    EdgeRole Role, string Port, long EdgeRevision = 0);
