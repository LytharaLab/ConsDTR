namespace ConsDTR.Runtime;

/// <summary>Exports only the complete authoritative prefix after an independently anchored final gate.</summary>
public static class SealedExporter
{
    public static SealedExportReceipt Export(RuntimeCertificate certificate, string path, ITaskVerifier verifier,
        EndAnchor expectedAnchor, long maxVerificationWork = 50_000, long maxBytes = 128_000_000)
    {
        ArgumentNullException.ThrowIfNull(certificate); ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(expectedAnchor);
        if (maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var report = IndependentVerifier.Verify(certificate, verifier, expectedAnchor, maxVerificationWork);
        if (!report.Accepted) throw new InvalidDataException("Sealed export gate rejected certificate: " + report.Reason);
        var json = certificate.Serialize(); var bytes = DtrCanonical.StrictUtf8.GetBytes(json);
        var debit = certificate.RuntimeJournal.Single(x => x.EventKind == "FINAL_GATE_ALLOWANCE");
        using var document = System.Text.Json.JsonDocument.Parse(debit.CanonicalPayload);
        var declaredMaxBytes = document.RootElement.GetProperty("MaxBytes").GetInt64();
        if (bytes.LongLength > Math.Min(maxBytes, declaredMaxBytes)) throw new RuntimeResourceException("Sealed export exceeds caller or sealed byte allowance.");
        AtomicArtifactWriter.Write(path, bytes);
        return new(Path.GetFullPath(path), certificate.Manifest.SessionId, DtrCanonical.Digest(certificate),
            expectedAnchor.AnchorId, bytes.LongLength, report);
    }

    public static RuntimeCertificate Load(string path, long maxBytes = 128_000_000) =>
        RuntimeCertificate.Deserialize(AtomicArtifactWriter.Read(path, maxBytes), maxBytes);
}
