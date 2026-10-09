namespace ConsDTR.Runtime;

public sealed record TaskInput(string InputId, ExactValue Value);

public sealed record TaskDefinition(string TaskId, string SemanticVersion, string CanonicalInput,
    string CanonicalQuery, IReadOnlyList<TaskInput> Inputs);
