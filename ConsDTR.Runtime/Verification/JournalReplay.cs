using System.Text.Json;

namespace ConsDTR.Runtime;

/// <summary>Reconstructs every declared persistent transition; never calls the online semantic kernel.</summary>
internal static class JournalReplay
{
    internal sealed record Replayed(RuntimeState State, GenesisPayload Genesis, SealPayload? Seal);

    public static Replayed Replay(IReadOnlyList<JournalEvent> journal, VerificationMeter meter, bool requireSeal)
    {
        if (journal.Count == 0) throw new InvalidDataException("Missing genesis.");
        var genesis = DtrCanonical.Parse<GenesisPayload>(journal[0].CanonicalPayload);
        if (genesis.ArchitectureId != "ConsDTRv1" || genesis.SpecRevision != "Draft-2" || genesis.JournalSchema != "ConsDTRv1-Replay-Draft-2"
            || genesis.KernelVersion != "ConsDTRv1-Kernel-Draft-2") throw new InvalidDataException("Unsupported architecture/schema.");
        RuntimeSession.ValidateOptions(genesis.Options);
        var options = genesis.Options;
        var state = new RuntimeState { Mode = options.Mode };
        var digest = string.Empty; long bytes = 0;
        var receivedReads = new List<ReadReceipt>(); var receivedWrites = new List<WriteReceipt>();
        var transportEdges = new List<DataEdge>(); var transportIndex = 0;
        ProbePayload? probe = null; bool probeRead = false; bool precheckAccepted = false; bool allowancePaid = false;
        SealPayload? prepared = null; SealPayload? seal = null;
        var inputDigest = DtrCanonical.Digest(new { genesis.Task.CanonicalInput, genesis.Task.CanonicalQuery, genesis.Task.TaskId, genesis.Task.SemanticVersion });
        for (var index = 0; index < journal.Count; index++)
        {
            var entry = journal[index];
            var eventBytes = DtrCanonical.Encode(entry).LongLength;
            bytes = checked(bytes + eventBytes);
            if (!meter.Charge(checked(1 + (eventBytes + DtrCanonical.Encode(state).LongLength + 63) / 64))) throw new InvalidDataException("Replay verification resource limit.");
            if (entry.EventSeq != index || entry.SessionId != options.SessionId || !Enum.IsDefined(entry.ExecutionMode)
                || entry.PrevEventDigest != digest || entry.EventDigest != entry.ComputeDigest()
                || entry.PreTopologyRevision != state.TopologyRevision || entry.PreExactStateRevision != state.ExactStateRevision
                || entry.PreStateDigest != state.Digest() || entry.OperationWorkCost < 0)
                throw new InvalidDataException("Broken continuous journal identity, digest, version or cost.");
            if (state.Phase == RuntimePhase.Sealed) throw new InvalidDataException("Execution continued after the authoritative seal.");
            if (index != 0 && entry.EventKind == "GENESIS") throw new InvalidDataException("Repeated genesis.");
            var next = state;
            long requiredCost = 0;
            void Phase(params RuntimePhase[] phases)
            {
                if (!phases.Contains(state.Phase)) throw new InvalidDataException("Illegal journal phase for " + entry.EventKind);
            }
            switch (entry.EventKind)
            {
                case "GENESIS":
                    if (index != 0) throw new InvalidDataException("Genesis must be first.");
                    next = ReconstructGenesis(genesis, inputDigest);
                    requiredCost = checked(1 + next.Slots.Length + next.ExactBytes / 64);
                    break;
                case "STEP_BEGIN":
                    Phase(RuntimePhase.Idle); Fields(entry, "Step");
                    if (state.ProbeOpen || state.StepsUsed >= options.MaxMicrosteps || Number(entry, "Step") != state.MicrostepId + 1)
                        throw new InvalidDataException("Invalid microstep begin or exhausted step budget.");
                    next = state with
                    {
                        MicrostepId = checked(state.MicrostepId + 1), StepsUsed = checked(state.StepsUsed + 1), Phase = RuntimePhase.Editing,
                        EdgeEditsThisStep = 0, SlotOperationsThisStep = 0, ControlReadsThisStep = 0, EditedEndpoints = [],
                        ControlReadSet = [], TransportSnapshotSeq = -1
                    };
                    receivedReads.Clear(); receivedWrites.Clear(); prepared = null; requiredCost = 1;
                    break;
                case "AUDIT":
                    Phase(RuntimePhase.Idle, RuntimePhase.Editing);
                    if (state.ProbeOpen) throw new InvalidDataException("Audit while probe is open.");
                    var audit = DtrCanonical.Parse<AuditPayload>(entry.CanonicalPayload);
                    if (string.IsNullOrWhiteSpace(audit.Kind)) throw new InvalidDataException("Empty audit kind.");
                    DtrCanonical.EncodeJson(audit.Data);
                    CheckDiscoveryIdentity(audit, state);
                    next = state with { AuditDigest = DtrCanonical.Digest(new { Previous = state.AuditDigest, Audit = audit }) };
                    // Detailed task-independent discovery tariffs are checked by DirectoryAuditVerifier.
                    requiredCost = entry.OperationWorkCost;
                    break;
                case "CONTROL_PROBE_OPEN":
                    Phase(RuntimePhase.Editing);
                    if (state.ProbeOpen) throw new InvalidDataException("Nested control probe.");
                    probe = DtrCanonical.Parse<ProbePayload>(entry.CanonicalPayload);
                    if (probe.ControlViewId != state.ControlViewId + 1 || probe.Handles.Count + state.ControlReadsThisStep > options.MaxControlReadsPerStep
                        || probe.Handles.Distinct().Count() != probe.Handles.Count)
                        throw new InvalidDataException("Invalid bounded control probe.");
                    foreach (var handle in probe.Handles) Live(state, handle);
                    probeRead = false; next = state with { ProbeOpen = true, ControlViewId = probe.ControlViewId }; requiredCost = 1;
                    break;
                case "CONTROL_PROBE_READ":
                    Phase(RuntimePhase.Editing);
                    if (!state.ProbeOpen || probe is null || probeRead || DtrCanonical.Digest(probe) != DtrCanonical.Digest(DtrCanonical.Parse<ProbePayload>(entry.CanonicalPayload)))
                        throw new InvalidDataException("Substituted/repeated control read set.");
                    next = state with
                    {
                        ControlReadSet = state.ControlReadSet.Concat(probe.Handles).Distinct().OrderBy(x => x.SlotId).ThenBy(x => x.Generation).ToArray(),
                        ControlReadsThisStep = checked(state.ControlReadsThisStep + probe.Handles.Count)
                    };
                    probeRead = true; requiredCost = Math.Max(1, probe.Handles.Count);
                    break;
                case "CONTROL_PROBE_CLOSE":
                    Phase(RuntimePhase.Editing);
                    if (!state.ProbeOpen || !probeRead || probe is null || DtrCanonical.Digest(probe) != DtrCanonical.Digest(DtrCanonical.Parse<ProbePayload>(entry.CanonicalPayload)))
                        throw new InvalidDataException("Unmatched control release.");
                    next = state with { ProbeOpen = false }; probe = null; requiredCost = 1;
                    break;
                case "TOPOLOGY_TRANSACTION":
                    Phase(RuntimePhase.Editing);
                    if (state.ProbeOpen) throw new InvalidDataException("Topology changed inside an open control view.");
                    var topology = DtrCanonical.Parse<TopologyPayload>(entry.CanonicalPayload);
                    // Declarative syntax/invariants are shared; semantic verifier is a separate implementation.
                    var structural = TopologyEngine.Evaluate(state, topology.Transaction, options);
                    if (structural.Result.Accepted != topology.Accepted || structural.Result.Changed != topology.Changed
                        || DtrCanonical.Digest(structural.Result.AddedEdges) != DtrCanonical.Digest(topology.AddedEdges))
                        throw new InvalidDataException("Topology batch claimed a different atomic transition.");
                    next = structural.State; requiredCost = checked(1 + topology.Transaction.Edits.Count);
                    break;
                case "TRANSPORT_BEGIN":
                    Phase(RuntimePhase.Editing); Fields(entry, "SnapshotSeq");
                    if (state.ProbeOpen || Number(entry, "SnapshotSeq") != entry.EventSeq) throw new InvalidDataException("Invalid transport snapshot.");
                    next = state with { Phase = RuntimePhase.Transporting, TransportSnapshotSeq = entry.EventSeq };
                    transportEdges = state.Edges.Where(e => e.Role == EdgeRole.WriteResult || e.Role != EdgeRole.NeuralMessage && Live(state, e.Source).RecordId is not null).OrderBy(e => e.EdgeId).ToList();
                    transportIndex = 0; receivedReads.Clear(); receivedWrites.Clear(); requiredCost = 1;
                    break;
                case "READ_RECEIPT":
                    Phase(RuntimePhase.Transporting);
                    if (transportIndex >= transportEdges.Count) throw new InvalidDataException("Unexpected extra receipt.");
                    var read = DtrCanonical.Parse<ReadReceipt>(entry.CanonicalPayload);
                    var readEdge = transportEdges[transportIndex++]; var source = Live(state, readEdge.Source); var receiver = Live(state, readEdge.Target);
                    if (readEdge.Role is EdgeRole.NeuralMessage or EdgeRole.WriteResult || source.RecordId is null) throw new InvalidDataException("Read receipt has no exact-data edge.");
                    var readRecord = Record(state, source.RecordId.Value);
                    var expectedRead = new ReadReceipt(options.SessionId, state.MicrostepId, state.TransportSnapshotSeq,
                        readRecord.RecordId, readRecord.ValueRevision, readEdge.EdgeId, state.TopologyRevision,
                        source.Handle, receiver.Handle, source.BindingRevision, receiver.BindingRevision, readEdge.EdgeRevision,
                        readEdge.Port, state.ControlViewId, entry.EventSeq);
                    if (read != expectedRead) throw new InvalidDataException("Read receipt does not match reconstructed exact snapshot.");
                    receivedReads.Add(read); requiredCost = checked(1 + DtrCanonical.Encode(readRecord.Value).LongLength / 64);
                    break;
                case "WRITE_RECEIPT":
                    Phase(RuntimePhase.Transporting);
                    if (transportIndex >= transportEdges.Count) throw new InvalidDataException("Unexpected extra write receipt.");
                    var write = DtrCanonical.Parse<WriteReceipt>(entry.CanonicalPayload); var writeEdge = transportEdges[transportIndex++];
                    var worker = Live(state, writeEdge.Source); var destination = Live(state, writeEdge.Target);
                    if (writeEdge.Role != EdgeRole.WriteResult) throw new InvalidDataException("Write receipt lacks WRITE_RESULT edge.");
                    var expectedWrite = new WriteReceipt(options.SessionId, state.MicrostepId, state.TransportSnapshotSeq,
                        worker.Handle, destination.Handle, destination.RecordId is null ? 0 : Record(state, destination.RecordId.Value).ValueRevision,
                        writeEdge.EdgeId, state.TopologyRevision, writeEdge.Port, worker.BindingRevision, destination.BindingRevision,
                        writeEdge.EdgeRevision, state.ControlViewId, entry.EventSeq);
                    if (write != expectedWrite) throw new InvalidDataException("Write receipt does not match reconstructed target binding.");
                    receivedWrites.Add(write); requiredCost = 1;
                    break;
                case "TRANSPORT_END":
                    Phase(RuntimePhase.Transporting); Fields(entry, "SnapshotSeq", "ReadCount", "WriteCount");
                    if (transportIndex != transportEdges.Count || Number(entry, "SnapshotSeq") != state.TransportSnapshotSeq
                        || Number(entry, "ReadCount") != receivedReads.Count || Number(entry, "WriteCount") != receivedWrites.Count)
                        throw new InvalidDataException("Incomplete transport receipt coverage.");
                    next = state with { Phase = RuntimePhase.Transported }; requiredCost = Math.Max(1, state.Edges.Length);
                    break;
                case "SEMANTIC_REJECT":
                case "SEMANTIC_COMMIT":
                    Phase(RuntimePhase.Transported);
                    var semantic = DtrCanonical.Parse<SemanticPayload>(entry.CanonicalPayload);
                    if (entry.EventKind == "SEMANTIC_REJECT")
                    {
                        if (semantic.Accepted || semantic.Proof is not null || semantic.Record is not null || semantic.Target is not null)
                            throw new InvalidDataException("Rejected semantic action changed exact state.");
                        next = state with { Phase = RuntimePhase.Committed };
                        requiredCost = checked(1 + semantic.Action.ReadReceipts.Count);
                    }
                    else
                    {
                        if (!semantic.Accepted || semantic.Proof is null || semantic.Record is null || semantic.Target is null) throw new InvalidDataException("Incomplete accepted semantic payload.");
                        ValidateCommit(state, semantic, receivedReads, receivedWrites, entry, genesis, inputDigest);
                        var valueBytes = DtrCanonical.Encode(semantic.Record.Value).LongLength;
                        if (state.Proofs.LongLength >= options.MaxProofs || valueBytes > options.MaxExactBytes - state.ExactBytes) throw new InvalidDataException("Commit exceeded exact/evidence capacity.");
                        next = state with
                        {
                            Slots = state.Slots.Select(x => x.Handle == semantic.Target.Handle ? semantic.Target : x).ToArray(),
                            Records = [..state.Records, semantic.Record], Proofs = [..state.Proofs, semantic.Proof],
                            ExactStateRevision = checked(state.ExactStateRevision + 1), ExactBytes = checked(state.ExactBytes + valueBytes), Phase = RuntimePhase.Committed
                        };
                        requiredCost = checked(1 + semantic.Action.ReadReceipts.Count + valueBytes / 64);
                    }
                    break;
                case "STEP_END":
                    Phase(RuntimePhase.Editing, RuntimePhase.Transported, RuntimePhase.Committed); Fields(entry, "Step");
                    if (state.ProbeOpen || Number(entry, "Step") != state.MicrostepId) throw new InvalidDataException("Unclosed step/control phase.");
                    next = state with { Phase = RuntimePhase.Idle, TransportSnapshotSeq = -1 }; requiredCost = 1;
                    break;
                case "MODE_CHANGE":
                    Phase(RuntimePhase.Idle); Fields(entry, "Before", "After");
                    var before = (ExecutionMode)Number(entry, "Before"); var after = (ExecutionMode)Number(entry, "After");
                    if (before != state.Mode || !Enum.IsDefined(after) || before == after || before == ExecutionMode.FairFallback && after == ExecutionMode.LearnedOnly
                        || before == ExecutionMode.OracleDiagnostic && after != before) throw new InvalidDataException("Mode change conceals runtime intervention.");
                    next = state with { Mode = after }; requiredCost = 1;
                    break;
                case "RECOVERY_ABORT_PHASE":
                    Phase(RuntimePhase.Editing, RuntimePhase.Transporting, RuntimePhase.Transported, RuntimePhase.Committed, RuntimePhase.Prepared);
                    Fields(entry, "InterruptedPhase", "Reason");
                    using (var recovery = JsonDocument.Parse(entry.CanonicalPayload))
                        if (recovery.RootElement.GetProperty("InterruptedPhase").GetString() != state.Phase.ToString())
                            throw new InvalidDataException("Recovery abort did not name the interrupted phase.");
                    if (state.Phase == RuntimePhase.Prepared && (precheckAccepted || allowancePaid)) throw new InvalidDataException("Recovery tried to unfreeze an accepted terminal tail.");
                    next = state with { Phase = state.Phase == RuntimePhase.Prepared ? RuntimePhase.Idle : RuntimePhase.Committed, ProbeOpen = false, TransportSnapshotSeq = -1 };
                    probe = null; prepared = null; receivedReads.Clear(); receivedWrites.Clear(); requiredCost = 1;
                    break;
                case "FAILURE_CLOSE":
                    var failure = DtrCanonical.Parse<FailurePayload>(entry.CanonicalPayload);
                    if (string.IsNullOrWhiteSpace(failure.Kind) || failure.Kind.Length > 128 || failure.PreviousPhase != state.Phase.ToString()
                        || failure.Reason is null || System.Text.Encoding.UTF8.GetByteCount(failure.Reason) > options.MaxValueBytes
                        || state.Phase == RuntimePhase.Prepared && (precheckAccepted || allowancePaid))
                        throw new InvalidDataException("Failure closure changed a frozen terminal tail or omitted phase provenance.");
                    var failureHadStep = state.Phase is RuntimePhase.Editing or RuntimePhase.Transporting or RuntimePhase.Transported or RuntimePhase.Committed;
                    next = state with { Phase = failureHadStep ? RuntimePhase.Committed : RuntimePhase.Idle, ProbeOpen = false, TransportSnapshotSeq = -1 };
                    probe = null; prepared = null; receivedReads.Clear(); receivedWrites.Clear();
                    requiredCost = checked(1 + System.Text.Encoding.UTF8.GetByteCount(failure.Reason) / 64);
                    break;
                case "OUTPUT_PREPARE":
                    Phase(RuntimePhase.Idle);
                    prepared = DtrCanonical.Parse<SealPayload>(entry.CanonicalPayload);
                    CheckOutput(prepared, state, options);
                    next = state with { Phase = RuntimePhase.Prepared }; precheckAccepted = false; allowancePaid = false; requiredCost = 1;
                    break;
                case "PRECHECK_REJECT":
                    Phase(RuntimePhase.Prepared); Fields(entry, "Reason", "UsedWork");
                    if (precheckAccepted || allowancePaid) throw new InvalidDataException("Attempt to unfreeze accepted fixed tail.");
                    next = state with { Phase = RuntimePhase.Idle }; prepared = null; requiredCost = checked(Number(entry, "UsedWork") + 1);
                    break;
                case "PRECHECK_ACCEPT":
                    Phase(RuntimePhase.Prepared); Fields(entry, "Reason", "UsedWork");
                    if (precheckAccepted || allowancePaid || prepared is null) throw new InvalidDataException("Repeated or unbound precheck.");
                    precheckAccepted = true; requiredCost = checked(Number(entry, "UsedWork") + 1);
                    break;
                case "FINAL_GATE_ALLOWANCE":
                    Phase(RuntimePhase.Prepared); Fields(entry, "Allowance", "MaxBytes");
                    if (!precheckAccepted || allowancePaid || Number(entry, "Allowance") != options.FinalGateWorkAllowance || Number(entry, "MaxBytes") != options.MaxExportBytes)
                        throw new InvalidDataException("Missing conservative final gate/export debit.");
                    allowancePaid = true; requiredCost = options.FinalGateWorkAllowance;
                    break;
                case "SEALED_EXPORT_CUT":
                    Phase(RuntimePhase.Prepared);
                    seal = DtrCanonical.Parse<SealPayload>(entry.CanonicalPayload);
                    if (!precheckAccepted || !allowancePaid || prepared is null || DtrCanonical.Digest(prepared) != DtrCanonical.Digest(seal)
                        || index != journal.Count - 1) throw new InvalidDataException("Seal cut changed the frozen output, omitted terminal phases or was not final.");
                    CheckOutput(seal, state, options); next = state with { Phase = RuntimePhase.Sealed }; requiredCost = 1;
                    break;
                default: throw new InvalidDataException("Unknown semantic journal event kind: " + entry.EventKind);
            }
            if (entry.OperationWorkCost != requiredCost || requiredCost < 0) throw new InvalidDataException("Operation tariff differs from reconstructed work.");
            var expectedDelta = checked(requiredCost + RuntimeAccounting.JournalWork(state, next, entry.CanonicalPayload));
            next = next with { WorkCost = checked(state.WorkCost + expectedDelta) };
            if (entry.DeltaWorkCost != expectedDelta || entry.CumulativeWorkCost != next.WorkCost || next.WorkCost > options.MaxWork
                || entry.PostTopologyRevision != next.TopologyRevision || entry.PostExactStateRevision != next.ExactStateRevision
                || entry.PostStateDigest != next.Digest() || entry.MicrostepId != next.MicrostepId || entry.Phase != next.Phase.ToString()
                || entry.ExecutionMode != next.Mode || bytes > options.MaxJournalBytes || index >= options.MaxJournalEvents)
                throw new InvalidDataException("Journal post-state, phase, budget or revision mismatch.");
            TopologyEngine.CheckInvariants(next, options);
            state = next; digest = entry.EventDigest;
        }
        if (requireSeal && (state.Phase != RuntimePhase.Sealed || seal is null)) throw new InvalidDataException("Missing complete authoritative genesis-to-seal prefix.");
        return new(state, genesis, seal);
    }

    private static RuntimeState ReconstructGenesis(GenesisPayload genesis, string inputDigest)
    {
        var options = genesis.Options; var definition = genesis.Task;
        if (definition.Inputs is null || definition.Inputs.Select(x => x.InputId).Distinct(StringComparer.Ordinal).Count() != definition.Inputs.Count)
            throw new InvalidDataException("Repeated input identities in genesis.");
        var slots = new List<SlotState>(); var records = new List<ExactRecord>(); var proofs = new List<ProofEvent>(); long exactBytes = 0;
        foreach (var input in definition.Inputs)
        {
            RuntimeSession.ValidateValue(input.Value, options);
            if (string.IsNullOrEmpty(input.InputId) || input.Value.Trust != ValueTrust.Verified) throw new InvalidDataException("Untrusted input root.");
            var id = (long)slots.Count; var handle = new SlotHandle(id, 0);
            slots.Add(new(handle, SlotKind.Observation, true, id)); records.Add(new(id, input.InputId, input.Value, 0, id, true));
            proofs.Add(new(id, options.SessionId, definition.SemanticVersion, "INPUT_REF", [], [], input.InputId,
                input.Value, 0, default, 0, [], null, handle, 0, [], -1, options.ModelCheckpointDigest,
                inputDigest, options.Mode, 0, -1, 0, 0));
            exactBytes = checked(exactBytes + DtrCanonical.Encode(input.Value).LongLength);
        }
        for (var i = 0; i < options.WorkerCount; i++) slots.Add(new(new(slots.Count, 0), SlotKind.Worker, true));
        for (var i = 0; i < options.RegisterCount; i++) slots.Add(new(new(slots.Count, 0), SlotKind.Register, true));
        var expected = new RuntimeState { Slots = slots.ToArray(), Records = records.ToArray(), Proofs = proofs.ToArray(), ExactBytes = exactBytes, Mode = options.Mode };
        if (exactBytes > options.MaxExactBytes || records.Count > options.MaxProofs || slots.Count > options.MaxSlots
            || DtrCanonical.Digest(expected) != DtrCanonical.Digest(genesis.InitialState)) throw new InvalidDataException("Genesis is not the canonical task-derived initial state.");
        return expected;
    }

    private static void ValidateCommit(RuntimeState state, SemanticPayload payload, List<ReadReceipt> reads,
        List<WriteReceipt> writes, JournalEvent entry, GenesisPayload genesis, string inputDigest)
    {
        var action = payload.Action; var proof = payload.Proof!; var record = payload.Record!; var target = payload.Target!;
        var options = genesis.Options;
        var worker = Live(state, action.Worker);
        if (worker.Kind != SlotKind.Worker || action.ReadReceipts.Count > options.MaxInDegree
            || action.ReadReceipts.Select(x => x.ReceiptJournalSeq).Distinct().Count() != action.ReadReceipts.Count)
            throw new InvalidDataException("Invalid worker or repeated/overdegree operands.");
        foreach (var receipt in action.ReadReceipts)
            if (receipt.Receiver != action.Worker || !reads.Contains(receipt)) throw new InvalidDataException("Committed operand has no exact current transport event.");
        if (!writes.Contains(action.WriteReceipt) || action.WriteReceipt.Worker != action.Worker || action.WriteReceipt.TargetRevisionBeforeWrite != 0)
            throw new InvalidDataException("Committed write lacks current WRITE_RESULT transport.");
        var oldTarget = Live(state, action.WriteReceipt.Target);
        if (oldTarget.Kind != SlotKind.Register || oldTarget.RecordId is not null || oldTarget.BindingRevision != action.WriteReceipt.TargetBindingRevision
            || worker.BindingRevision != action.WriteReceipt.WorkerBindingRevision) throw new InvalidDataException("Committed target is read-only, occupied or stale.");
        var expectedTarget = oldTarget with { RecordId = state.Records.LongLength, BindingRevision = checked(oldTarget.BindingRevision + 1), MetadataRevision = checked(oldTarget.MetadataRevision + 1) };
        if (target != expectedTarget || record.RecordId != state.Records.LongLength || record.ValueRevision != 1 || record.ProofId != state.Proofs.LongLength
            || record.IsInput || record.Value != proof.ExactOutput || record.InputId != (proof.RuleOrConstraintInputId ?? string.Empty))
            throw new InvalidDataException("Exact output binding/record metadata differs from the action.");
        RuntimeSession.ValidateValue(record.Value, options);
        var inputIds = action.ReadReceipts.Select(x => Record(state, x.SourceRecordId).ProofId).ToArray();
        var versions = action.ReadReceipts.Select(x => x.ValueRevision).ToArray();
        if (proof.ProofId != state.Proofs.LongLength || proof.SessionId != options.SessionId || proof.TaskSemanticsVersion != genesis.Task.SemanticVersion
            || proof.Operation != action.Operation || !proof.InputProofIds.SequenceEqual(inputIds) || !proof.InputValueRevisions.SequenceEqual(versions)
            || proof.InputProofIds.Any(x => x < 0 || x >= proof.ProofId) || proof.Worker != worker.Handle || proof.TopologyRevision != state.TopologyRevision
            || !proof.DataReadReceipts.SequenceEqual(action.ReadReceipts) || proof.WriteReceipt != action.WriteReceipt || proof.TargetHandle != target.Handle
            || proof.TargetValueRevision != 1 || proof.OutputRevision != 1 || !proof.ControlReadSet.SequenceEqual(state.ControlReadSet)
            || proof.CommitStep != state.MicrostepId || proof.ModelManifestId != options.ModelCheckpointDigest || proof.InputDigest != inputDigest
            || proof.ExecutionMode != state.Mode || proof.CommitJournalSeq != entry.EventSeq || proof.TransportSnapshotSeq != state.TransportSnapshotSeq
            || proof.TargetBindingRevision != target.BindingRevision || proof.ControlViewId != state.ControlViewId
            || proof.ProposedCandidate != action.Candidate)
            throw new InvalidDataException("Evidence metadata, DAG, value revisions or provenance were substituted.");
        // Candidate proposal must stay a candidate; the independent task verifier checks operation semantics.
        if (action.Operation == "PROPOSE_VALUE" && (action.Candidate is null || proof.ExactOutput != action.Candidate with { Trust = ValueTrust.Candidate }))
            throw new InvalidDataException("Candidate proposal promoted into a verified fact.");
        if (action.Operation is "PROPOSE_CLOSED_SET" or "PROPOSE_CUT" or "PROPOSE_ASSIGNMENT"
            && (action.Candidate is null || action.Candidate.Trust != ValueTrust.Candidate || proof.ExactOutput != action.Candidate))
            throw new InvalidDataException("Typed candidate proposal differs from the exact logged action argument.");
    }

    private static void CheckOutput(SealPayload payload, RuntimeState state, SessionOptions options)
    {
        RuntimeSession.ValidateValue(payload.Output, options);
        if (payload.ProofRootDigest != DtrCanonical.Digest(state.Proofs) || payload.Mode != state.Mode
            || payload.ModelCheckpointDigest != options.ModelCheckpointDigest || payload.FinalGateAllowance != options.FinalGateWorkAllowance)
            throw new InvalidDataException("Prepared output differs from the frozen proof, mode or checkpoint.");
    }
    private static SlotState Live(RuntimeState state, SlotHandle handle) => state.Slots.SingleOrDefault(x => x.Handle == handle && x.Active)
        ?? throw new InvalidDataException("Replayed handle is inactive or stale.");
    private static ExactRecord Record(RuntimeState state, long id) => state.Records.SingleOrDefault(x => x.RecordId == id)
        ?? throw new InvalidDataException("Replayed exact record does not exist.");
    private static long Number(JournalEvent entry, string name)
    { using var document = JsonDocument.Parse(entry.CanonicalPayload); return document.RootElement.GetProperty(name).GetInt64(); }
    private static void Fields(JournalEvent entry, params string[] names)
    {
        using var document = JsonDocument.Parse(entry.CanonicalPayload);
        if (!document.RootElement.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Unknown or omitted semantic payload field.");
    }

    private static void CheckDiscoveryIdentity(AuditPayload audit, RuntimeState state)
    {
        void Current(Discovery.CandidateHeader header)
        {
            var slot = state.Slots.SingleOrDefault(x => x.Handle.SlotId == header.Slot);
            if (slot is null || slot.Handle.Generation != header.Generation || slot.BindingRevision != header.BindingRevision
                || slot.MetadataRevision != header.MetadataRevision || slot.Kind.ToString() != header.Bucket
                || slot.Active != header.Active || !slot.Active && header.IsTarget)
                throw new InvalidDataException("Discovery header does not match the reconstructed slot/binding/metadata identity.");
        }
        if (audit.Kind == "DIRECTORY_UPSERT") Current(DtrCanonical.Parse<Discovery.CandidateHeader>(audit.Data));
        else if (audit.Kind == "DIRECTORY_PAGE")
        {
            using var document = JsonDocument.Parse(audit.Data);
            var item = document.RootElement;
            if (item.GetProperty("Step").GetInt64() != state.MicrostepId) throw new InvalidDataException("Directory page was attributed to a different microstep.");
            var page = DtrCanonical.Parse<Discovery.DirectoryPage>(item.GetProperty("Page").GetRawText());
            foreach (var header in page.Candidates) Current(header);
            if (page.Candidates.Count != 0) Current(page.Target);
        }
        else if (audit.Kind == "DIRECTORY_EPOCH_BEGIN")
        {
            using var document = JsonDocument.Parse(audit.Data);
            foreach (var target in document.RootElement.GetProperty("Targets").EnumerateArray())
                Current(DtrCanonical.Parse<Discovery.CandidateHeader>(target.GetProperty("Target").GetRawText()));
        }
        else if (audit.Kind == "CANDIDATE_PIN")
        {
            using var document = JsonDocument.Parse(audit.Data);
            Current(DtrCanonical.Parse<Discovery.CandidateHeader>(document.RootElement.GetProperty("Header").GetRawText()));
        }
    }
}
