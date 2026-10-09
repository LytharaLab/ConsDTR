namespace ConsDTR.Runtime;

public sealed record RuntimeCertificate(CertificateManifest Manifest, TaskDefinition Task,
    IReadOnlyList<ProofEvent> Proofs, IReadOnlyList<JournalEvent> RuntimeJournal, EndAnchor EndAnchor)
{
    public string Serialize() => DtrCanonical.Json(this);
    public static RuntimeCertificate Deserialize(string json, long maxBytes = 128_000_000)
    {
        if (maxBytes < 0 || DtrCanonical.StrictUtf8.GetByteCount(json) > maxBytes)
            throw new RuntimeResourceException("Certificate input exceeds explicit byte allowance.");
        return DtrCanonical.Parse<RuntimeCertificate>(json);
    }
}
