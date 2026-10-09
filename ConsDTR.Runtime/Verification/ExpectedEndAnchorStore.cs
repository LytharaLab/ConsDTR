using System.Security.Cryptography;

namespace ConsDTR.Runtime;

/// <summary>Persists a runtime-obtained external anchor. The caller must control and trust the selected anchor file.</summary>
public static class ExpectedEndAnchorStore
{
    private sealed record StoredAnchor(string ArchitectureId, string SpecRevision, EndAnchor ExpectedEndAnchor);

    public static void Save(RuntimeSession trustedRuntime, string path, long maxBytes = 65_536)
    {
        ArgumentNullException.ThrowIfNull(trustedRuntime);
        var anchor = trustedRuntime.IsSealed ? trustedRuntime.TrustedEndAnchor : null;
        if (anchor is null) throw new InvalidOperationException("An external anchor can only be saved from an authoritatively sealed live runtime.");
        Validate(anchor);
        var bytes = DtrCanonical.StrictUtf8.GetBytes(DtrCanonical.Json(new StoredAnchor("ConsDTRv1", "Draft-2", anchor)));
        if (bytes.LongLength > maxBytes) throw new RuntimeResourceException("External anchor exceeds storage byte allowance.");
        AtomicArtifactWriter.Write(path, bytes);
    }

    /// <summary>Trust comes from the caller's choice/protection of this file, not from a certificate-supplied key or head.</summary>
    public static EndAnchor Load(string trustedAnchorPath, long maxBytes = 65_536)
    {
        var stored = DtrCanonical.Parse<StoredAnchor>(AtomicArtifactWriter.Read(trustedAnchorPath, maxBytes));
        if (stored.ArchitectureId != "ConsDTRv1" || stored.SpecRevision != "Draft-2") throw new InvalidDataException("Unsupported external anchor schema.");
        Validate(stored.ExpectedEndAnchor);
        return stored.ExpectedEndAnchor;
    }

    private static void Validate(EndAnchor anchor)
    {
        if (anchor is null) throw new InvalidDataException("Missing external authority anchor.");
        static bool Digest(string text) => text is { Length: 64 } && text.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');
        if (anchor.SessionId == Guid.Empty || anchor.LastSeq < 0 || !Enum.IsDefined(anchor.Mode)
            || !Digest(anchor.GenesisDigest) || !Digest(anchor.TerminalEventDigest) || !Digest(anchor.OutputDigest) || !Digest(anchor.ProofRootDigest)
            || string.IsNullOrWhiteSpace(anchor.AuthorityId) || anchor.AuthorityId.Length > 128
            || string.IsNullOrEmpty(anchor.CheckpointDigest) || !Guid.TryParseExact(anchor.AnchorId, "N", out _))
            throw new InvalidDataException("Malformed external authority anchor.");
        try
        {
            using var key = ECDsa.Create(); key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(anchor.PublicKey), out _);
            if (!key.VerifyData(anchor.SigningBytes(), Convert.FromBase64String(anchor.Signature), HashAlgorithmName.SHA256))
                throw new InvalidDataException("External authority anchor signature is invalid.");
        }
        catch (Exception error) when (error is CryptographicException or FormatException or ArgumentException)
        { throw new InvalidDataException("Malformed authority public key or signature.", error); }
    }
}
