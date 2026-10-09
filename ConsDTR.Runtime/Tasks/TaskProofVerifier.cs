using System.Globalization;
using System.Numerics;

namespace ConsDTR.Runtime.Tasks;

/// <summary>Independent pure semantic replay. It never reads neural weights, topology, online accept flags, or a solver.</summary>
internal abstract class TaskProofVerifier : ITaskVerifier
{
    protected abstract TaskDefinition ReconstructDefinition(TaskDefinition supplied);
    protected abstract bool VerifyTaskOperation(TaskDefinition input, ProofEvent proof, IReadOnlyList<CheckedProof> parents, out string reason);
    protected abstract SemanticVerification VerifyOutput(TaskDefinition input, ExactValue output);

    public SemanticVerification Verify(TaskDefinition input, IReadOnlyList<ProofEvent> proofs, ExactValue output, VerificationMeter meter)
    {
        try
        {
            if (!meter.Charge(1L + input.CanonicalInput.Length + input.CanonicalQuery.Length))
                return new(false, "Semantic verification budget exhausted while binding input.");
            var expected = ReconstructDefinition(input);
            if (input.TaskId != expected.TaskId || input.SemanticVersion != expected.SemanticVersion ||
                input.CanonicalInput != expected.CanonicalInput || input.CanonicalQuery != expected.CanonicalQuery ||
                input.Inputs.Count != expected.Inputs.Count || !input.Inputs.SequenceEqual(expected.Inputs))
                return new(false, "Input roots, normalized input or exact output predicate do not match the task semantics.");
            var roots = expected.Inputs.ToDictionary(root => root.InputId, root => root.Value, StringComparer.Ordinal);
            var checkedProofs = new Dictionary<long, CheckedProof>();
            long previousId = -1;
            foreach (var proof in proofs)
            {
                if (!meter.Charge(1L + input.CanonicalInput.Length + input.CanonicalQuery.Length + proof.ExactOutput.Data.Length +
                    (proof.ProposedCandidate?.Data.Length ?? 0) + proof.InputProofIds.Count))
                    return new(false, "Semantic verification budget exhausted while replaying evidence.");
                if (proof.ProofId <= previousId || checkedProofs.ContainsKey(proof.ProofId) || proof.TaskSemanticsVersion != expected.SemanticVersion ||
                    proof.InputProofIds.Any(id => !checkedProofs.ContainsKey(id)))
                    return new(false, "Evidence has a duplicate, cyclic, future, reordered or semantically incompatible dependency.");
                previousId = proof.ProofId;
                var parents = proof.InputProofIds.Select(id => checkedProofs[id]).ToArray();
                string? rootInputId = null;
                bool accepted;
                string reason;
                if (proof.Operation == "INPUT_REF")
                {
                    rootInputId = proof.RuleOrConstraintInputId;
                    accepted = rootInputId is not null && roots.TryGetValue(rootInputId, out var root) && root == proof.ExactOutput &&
                        (parents.Length == 0 || (parents.Length == 1 && parents[0].RootInputId == rootInputId && parents[0].Value == root));
                    reason = "Input reference does not match the complete immutable input binding.";
                }
                else if (proof.Operation == "COPY")
                {
                    accepted = parents.Length == 1 && parents[0].Value == proof.ExactOutput;
                    if (accepted) rootInputId = parents[0].RootInputId;
                    reason = "COPY changes value or provenance trust.";
                }
                else if (proof.Operation == "PROPOSE_VALUE")
                {
                    accepted = parents.Length == 0 && proof.ExactOutput.Trust == ValueTrust.Candidate;
                    reason = "A proposal must remain unverified candidate data.";
                }
                else if (proof.Operation is "EQ" or "COMPARE" or "ADD" or "SUB" or "SELECT")
                {
                    accepted = VerifyBasic(proof, parents);
                    reason = "Basic exact operation or trust propagation is invalid.";
                    if (accepted && proof.Operation == "SELECT")
                        rootInputId = parents[0].Value.Data == "true" ? parents[1].RootInputId : parents[2].RootInputId;
                }
                else
                    accepted = VerifyTaskOperation(expected, proof, parents, out reason);
                if (!accepted)
                    return new(false, $"Proof {proof.ProofId}: {reason}");
                checkedProofs.Add(proof.ProofId, new CheckedProof(proof.ExactOutput, rootInputId));
            }
            if (!meter.Charge(1L + input.CanonicalInput.Length + input.CanonicalQuery.Length + output.Data.Length))
                return new(false, "Semantic verification budget exhausted at the output gate.");
            if (output.Trust != ValueTrust.Verified || !checkedProofs.Values.Any(proof => proof.Value == output))
                return new(false, "The exact final output is not a committed verified evidence value.");
            return VerifyOutput(expected, output);
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException or NullReferenceException or IndexOutOfRangeException or KeyNotFoundException or FormatException or OverflowException)
        { return new(false, "Malformed semantic certificate: " + error.Message); }
    }

    private static bool VerifyBasic(ProofEvent proof, IReadOnlyList<CheckedProof> parents)
    {
        var values = parents.Select(parent => parent.Value).ToArray();
        if (proof.Operation == "SELECT")
            return values.Length == 3 && values[0].Type == "bool" && values[0].Trust == ValueTrust.Verified &&
                values[0].Data is "true" or "false" && values[1].Type == values[2].Type && values[1].Trust == values[2].Trust &&
                proof.ExactOutput == (values[0].Data == "true" ? values[1] : values[2]);
        if (values.Length != 2 || values[0].Type != values[1].Type)
            return false;
        var trust = values.Any(value => value.Trust == ValueTrust.Candidate) ? ValueTrust.Candidate : ValueTrust.Verified;
        if (proof.Operation == "EQ")
            return proof.ExactOutput == new ExactValue("bool", values[0].Data == values[1].Data ? "true" : "false", trust);
        if (values[0].Type != "int" || !TryInteger(values[0].Data, out var left) || !TryInteger(values[1].Data, out var right))
            return false;
        var result = proof.Operation switch { "ADD" => left + right, "SUB" => left - right, "COMPARE" => new BigInteger(left.CompareTo(right)), _ => throw new ArgumentException("Unknown exact operation.") };
        return proof.ExactOutput == new ExactValue("int", result.ToString(CultureInfo.InvariantCulture), trust);
    }

    private static bool TryInteger(string data, out BigInteger value) =>
        BigInteger.TryParse(data, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value) && value.ToString(CultureInfo.InvariantCulture) == data;

    protected static CheckedProof One(IReadOnlyList<CheckedProof> parents, string type, bool verified = true)
    {
        var matches = parents.Where(parent => parent.Value.Type == type).ToArray();
        if (matches.Length != 1 || (verified && matches[0].Value.Trust != ValueTrust.Verified))
            throw new ArgumentException("Missing, duplicate or unverified typed semantic premise: " + type);
        return matches[0];
    }

    protected static bool ExactOutput(ProofEvent proof, string type, object payload, ValueTrust trust = ValueTrust.Verified) =>
        proof.ExactOutput == new ExactValue(type, TaskJson.Write(payload), trust);

    protected sealed record CheckedProof(ExactValue Value, string? RootInputId);
}
