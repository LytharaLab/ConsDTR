namespace ConsDTR.Runtime;

internal sealed record FailurePayload(string Kind, string Reason, string PreviousPhase);
