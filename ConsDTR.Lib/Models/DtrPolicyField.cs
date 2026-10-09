namespace ConsDTR.Lib.Models;

/// <summary>Every stochastic categorical field is recorded in the joint action likelihood.</summary>
public enum DtrPolicyField
{
    Intent = 0,
    EditKind = 1,
    Operation = 2,
    Port = 3,
    Relation = 4,
    Stop = 5,
    Bit = 6,
    SlotType = 7,
    SequenceLength = 8,
    Pointer = 9,
    ContinueIntent = 10
}
