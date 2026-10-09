using System.Security.Cryptography;

namespace ConsDTR.Runtime;

public static class IndependentVerifier
{
    /// <summary>The expected anchor must originate outside the certificate exporter.</summary>
    public static VerificationReport Verify(RuntimeCertificate certificate, ITaskVerifier semanticVerifier,
        EndAnchor? expectedAnchor, long maxVerificationWork = 50_000)
    {
        var meter = new VerificationMeter(maxVerificationWork);
        SemanticVerification semantic;
        try { semantic = semanticVerifier.Verify(certificate.Task, certificate.Proofs, certificate.Manifest.ExactOutput, meter); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or InvalidOperationException or OverflowException or FormatException
            or System.Text.Json.JsonException or NullReferenceException or KeyNotFoundException or IndexOutOfRangeException)
        { semantic = new(false, error.Message); }
        if (meter.Exhausted) return new(false, false, false, "Semantic verification allowance exhausted.", false, meter.UsedWork);
        try
        {
            var replayed = JournalReplay.Replay(certificate.RuntimeJournal, meter, requireSeal: true);
            Discovery.DirectoryAuditVerifier.Verify(certificate.RuntimeJournal);
            var manifest = certificate.Manifest; var tail = certificate.RuntimeJournal[^1]; var initial = certificate.RuntimeJournal[0];
            var inputDigest = DtrCanonical.Digest(new { certificate.Task.CanonicalInput, certificate.Task.CanonicalQuery, certificate.Task.TaskId, certificate.Task.SemanticVersion });
            if (manifest.ArchitectureId != "ConsDTRv1" || manifest.SpecRevision != "Draft-2" || manifest.JournalSchema != "ConsDTRv1-Replay-Draft-2"
                || manifest.KernelVersion != "ConsDTRv1-Kernel-Draft-2" || manifest.SessionId != replayed.Genesis.Options.SessionId
                || manifest.TaskVersion != certificate.Task.SemanticVersion || manifest.GenesisDigest != initial.EventDigest
                || DtrCanonical.Digest(certificate.Task) != DtrCanonical.Digest(replayed.Genesis.Task)
                || DtrCanonical.Digest(certificate.Proofs) != DtrCanonical.Digest(replayed.State.Proofs)
                || manifest.ProofRootDigest != DtrCanonical.Digest(certificate.Proofs) || manifest.ExactOutput != replayed.Seal!.Output
                || manifest.FirstSeq != 0 || manifest.LastSeq != tail.EventSeq || manifest.EventCount != certificate.RuntimeJournal.Count
                || manifest.InputQueryDigest != inputDigest || manifest.TerminalTopologyRevision != replayed.State.TopologyRevision
                || manifest.TerminalExactStateRevision != replayed.State.ExactStateRevision || manifest.TerminalStateDigest != tail.PostStateDigest
                || manifest.TerminalEventDigest != tail.EventDigest || manifest.DeclaredWorkCost != replayed.State.WorkCost
                || manifest.ExecutionMode != replayed.State.Mode || manifest.ModelCheckpointDigest != replayed.Genesis.Options.ModelCheckpointDigest
                || manifest.ExportAuthorityId != replayed.Genesis.Options.ExportAuthorityId || manifest.EndAnchorId != certificate.EndAnchor.AnchorId)
                throw new InvalidDataException("Certificate manifest, exact output or evidence differs from the complete reconstructed prefix.");
            if (DtrCanonical.Encode(certificate).LongLength > replayed.Genesis.Options.MaxExportBytes
                || DtrCanonical.StrictUtf8.GetByteCount(certificate.Serialize()) > replayed.Genesis.Options.MaxExportBytes)
                throw new InvalidDataException("Certificate export size allowance exceeded.");
            var bound = CheckAnchor(certificate, expectedAnchor, meter);
            return new(semantic.Accepted, true, bound, !semantic.Accepted ? semantic.Reason : bound ? "Complete semantic, route and authority checks passed." : "No matching independently trusted end anchor.",
                semantic.IsNegative, meter.UsedWork);
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or InvalidOperationException or OverflowException or FormatException or CryptographicException
            or System.Text.Json.JsonException or NullReferenceException or KeyNotFoundException or IndexOutOfRangeException)
        { return new(semantic.Accepted, false, false, error.Message, semantic.IsNegative, meter.UsedWork); }
    }

    public static SemanticVerification VerifySemantics(RuntimeCertificate certificate, ITaskVerifier verifier, long maxWork = 50_000)
    {
        var meter = new VerificationMeter(maxWork);
        var result = verifier.Verify(certificate.Task, certificate.Proofs, certificate.Manifest.ExactOutput, meter);
        return meter.Exhausted ? new(false, "Semantic verification resource limit.") : result;
    }

    private static bool CheckAnchor(RuntimeCertificate certificate, EndAnchor? expected, VerificationMeter meter)
    {
        if (!meter.Charge(32)) throw new InvalidDataException("Authority verification allowance exhausted.");
        if (expected is null || expected != certificate.EndAnchor) return false;
        var anchor = expected; var manifest = certificate.Manifest;
        if (anchor.SessionId != manifest.SessionId || anchor.GenesisDigest != manifest.GenesisDigest || anchor.LastSeq != manifest.LastSeq
            || anchor.TerminalEventDigest != manifest.TerminalEventDigest || anchor.OutputDigest != DtrCanonical.Digest(manifest.ExactOutput)
            || anchor.ProofRootDigest != manifest.ProofRootDigest || anchor.Mode != manifest.ExecutionMode || anchor.CheckpointDigest != manifest.ModelCheckpointDigest
            || anchor.AuthorityId != manifest.ExportAuthorityId || anchor.AnchorId != manifest.EndAnchorId) return false;
        using var key = ECDsa.Create(); key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(anchor.PublicKey), out _);
        return key.VerifyData(anchor.SigningBytes(), Convert.FromBase64String(anchor.Signature), HashAlgorithmName.SHA256);
    }
}
