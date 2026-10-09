namespace ConsDTR.Runtime;

public enum ValueTrust { Candidate, Verified }

public sealed record ExactValue(string Type, string Data, ValueTrust Trust = ValueTrust.Verified);
