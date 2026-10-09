using System.Collections.Immutable;

namespace ConsDTR.Runtime.Tasks;

/// <summary>A reference scheduler proposal, executed through ordinary topology, transport and kernel interfaces.</summary>
public sealed record TaskPlannedAction
{
    public string ResultBinding { get; }
    public string Operation { get; }
    public ImmutableArray<string> OperandBindings { get; }
    public ExactValue? Candidate { get; }
    public string? Argument { get; }

    public TaskPlannedAction(string resultBinding, string operation, IEnumerable<string> operandBindings,
        ExactValue? candidate = null, string? argument = null)
    {
        ResultBinding = resultBinding;
        Operation = operation;
        OperandBindings = operandBindings.ToImmutableArray();
        Candidate = candidate;
        Argument = argument;
    }
}
