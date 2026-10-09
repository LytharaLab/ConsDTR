namespace ConsDTR.Runtime.Tasks;

/// <summary>An exact walk, with the input edge identity for each extension.</summary>
internal sealed record PathValue(string Fingerprint, string[] Vertices, string[] EdgeIds);
