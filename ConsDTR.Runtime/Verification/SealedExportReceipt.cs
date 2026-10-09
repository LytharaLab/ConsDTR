namespace ConsDTR.Runtime;

public sealed record SealedExportReceipt(string Path, Guid SessionId, string CertificateDigest,
    string ExpectedEndAnchorId, long SerializedBytes, VerificationReport Verification);
