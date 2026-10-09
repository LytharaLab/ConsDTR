namespace ConsDTR.Runtime;

public sealed record EndAnchor(Guid SessionId, string GenesisDigest, long LastSeq,
    string TerminalEventDigest, string OutputDigest, string ProofRootDigest,
    ExecutionMode Mode, string CheckpointDigest, string AuthorityId, string AnchorId,
    string PublicKey, string Signature)
{
    public byte[] SigningBytes() => DtrCanonical.Encode(this with { Signature = string.Empty });
}
