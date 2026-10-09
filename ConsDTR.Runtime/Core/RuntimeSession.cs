using System.Security.Cryptography;
using System.Text;

namespace ConsDTR.Runtime;

/// <summary>Exclusive mutation authority for graph, exact records, receipts, proofs and accounting.</summary>
public sealed class RuntimeSession : IDisposable
{
    private readonly object sync = new();
    private readonly IKernelTask task;
    private readonly List<JournalEvent> journal = [];
    private readonly ECDsa authority = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private RuntimeState state;
    private long journalBytes;
    private TransportFrame? transport;
    private bool disposed;
    public SessionOptions Options { get; }
    public TaskDefinition Task { get; }
    public EndAnchor? TrustedEndAnchor { get; private set; }
    public string InputDigest { get; }
    public bool IsSealed => state.Phase == RuntimePhase.Sealed;
    public IReadOnlyList<JournalEvent> Journal { get { lock (sync) return Array.AsReadOnly(journal.ToArray()); } }
    public IReadOnlyDictionary<string, SlotHandle> InputSlots { get; }
    public IReadOnlyList<SlotState> Slots => Snapshot().Slots;
    public IReadOnlyList<DataEdge> Edges => Snapshot().Edges;
    public IReadOnlyList<ExactRecord> Records => Snapshot().Records;
    public IReadOnlyList<ProofEvent> Proofs => Snapshot().Proofs;

    private RuntimeSession(IKernelTask task, SessionOptions options)
    {
        this.task = task; Options = options;
        ValidateOptions(options);
        var copiedTask = DtrCanonical.Parse<TaskDefinition>(DtrCanonical.Json(task.Definition));
        Task = copiedTask with { Inputs = Array.AsReadOnly(copiedTask.Inputs.ToArray()) };
        ValidateTask(Task, options);
        InputDigest = DtrCanonical.Digest(new { Task.CanonicalInput, Task.CanonicalQuery, Task.TaskId, Task.SemanticVersion });
        var slots = new List<SlotState>(); var records = new List<ExactRecord>(); var proofs = new List<ProofEvent>();
        var inputs = new Dictionary<string, SlotHandle>(StringComparer.Ordinal);
        long exactBytes = 0;
        foreach (var input in Task.Inputs)
        {
            var id = (long)slots.Count; var handle = new SlotHandle(id, 0);
            slots.Add(new(handle, SlotKind.Observation, true, id));
            inputs.Add(input.InputId, handle);
            records.Add(new(id, input.InputId, input.Value, 0, id, true));
            proofs.Add(new(id, options.SessionId, Task.SemanticVersion, "INPUT_REF", [], [], input.InputId,
                input.Value, 0, default, 0, [], null, handle, 0, [], -1, options.ModelCheckpointDigest,
                InputDigest, options.Mode, 0, -1, 0, 0));
            exactBytes = checked(exactBytes + DtrCanonical.Encode(input.Value).LongLength);
        }
        for (var i = 0; i < options.WorkerCount; i++) slots.Add(new(new(slots.Count, 0), SlotKind.Worker, true));
        for (var i = 0; i < options.RegisterCount; i++) slots.Add(new(new(slots.Count, 0), SlotKind.Register, true));
        if (slots.Count > options.MaxSlots || records.Count > options.MaxProofs || exactBytes > options.MaxExactBytes)
            throw new RuntimeResourceException("Initial task exceeds slot, proof or exact-data capacity.");
        InputSlots = new System.Collections.ObjectModel.ReadOnlyDictionary<string, SlotHandle>(inputs);
        state = new() { Mode = options.Mode };
        var initial = state with { Slots = slots.ToArray(), Records = records.ToArray(), Proofs = proofs.ToArray(), ExactBytes = exactBytes };
        TopologyEngine.CheckInvariants(initial, options);
        var genesis = new GenesisPayload("ConsDTRv1", "Draft-2", "ConsDTRv1-Replay-Draft-2", "ConsDTRv1-Kernel-Draft-2", Task, options, initial);
        if (options.JournalPath is not null && File.Exists(options.JournalPath) && new FileInfo(options.JournalPath).Length != 0)
            throw new IOException("New session requires a new or empty WAL path.");
        Append("GENESIS", genesis, initial, checked(1 + slots.Count + exactBytes / 64));
    }

    public static RuntimeSession Create(IKernelTask task, SessionOptions? options = null) => new(task, options ?? new());

    private RuntimeSession(IKernelTask task, JournalReplay.Replayed replayed, IReadOnlyList<JournalEvent> prefix)
    {
        this.task = task;
        Options = replayed.Genesis.Options;
        var copied = replayed.Genesis.Task;
        Task = copied with { Inputs = Array.AsReadOnly(copied.Inputs.ToArray()) };
        if (DtrCanonical.Digest(task.Definition) != DtrCanonical.Digest(Task)) throw new InvalidDataException("Recovery task differs from journal genesis.");
        state = replayed.State with
        {
            Proofs = replayed.State.Proofs.Select(proof => proof with
            {
                InputProofIds = Array.AsReadOnly(proof.InputProofIds.ToArray()),
                InputValueRevisions = Array.AsReadOnly(proof.InputValueRevisions.ToArray()),
                DataReadReceipts = Array.AsReadOnly(proof.DataReadReceipts.ToArray()),
                ControlReadSet = Array.AsReadOnly(proof.ControlReadSet.ToArray())
            }).ToArray()
        };
        InputDigest = DtrCanonical.Digest(new { Task.CanonicalInput, Task.CanonicalQuery, Task.TaskId, Task.SemanticVersion });
        InputSlots = new System.Collections.ObjectModel.ReadOnlyDictionary<string, SlotHandle>(state.Records.Where(x => x.IsInput)
            .ToDictionary(x => x.InputId, x => state.Slots.Single(s => s.RecordId == x.RecordId).Handle, StringComparer.Ordinal));
        journal.AddRange(prefix); journalBytes = prefix.Sum(x => DtrCanonical.Encode(x).LongLength);
        if (state.Phase == RuntimePhase.Transported)
        {
            var current = prefix.Where(x => x.EventSeq > state.TransportSnapshotSeq).ToArray();
            transport = new(state.TransportSnapshotSeq, state.TopologyRevision,
                Array.AsReadOnly(current.Where(x => x.EventKind == "READ_RECEIPT").Select(x => DtrCanonical.Parse<ReadReceipt>(x.CanonicalPayload)).ToArray()),
                Array.AsReadOnly(current.Where(x => x.EventKind == "WRITE_RECEIPT").Select(x => DtrCanonical.Parse<WriteReceipt>(x.CanonicalPayload)).ToArray()));
        }
    }

    /// <summary>Restores exact/graph state from a complete committed prefix. Neural state and RNG belong to the Lib checkpoint.</summary>
    public static RuntimeSession Recover(IKernelTask task, IReadOnlyList<JournalEvent> committedPrefix,
        string expectedPrefixHead, long verificationAllowance = 1_000_000)
    {
        if (committedPrefix.Count == 0 || committedPrefix[^1].EventDigest != expectedPrefixHead)
            throw new InvalidDataException("Recovery prefix does not match independently expected journal head.");
        var replayed = JournalReplay.Replay(committedPrefix, new VerificationMeter(verificationAllowance), requireSeal: false);
        Discovery.DirectoryAuditVerifier.Verify(committedPrefix);
        if (replayed.State.Proofs.Length > task.Definition.Inputs.Count)
        {
            // Verify the complete derived evidence by a committed verified value; task-specific output predicates
            // are intentionally not asserted by recovery. Full semantic validation occurs at the final gate.
            foreach (var value in replayed.State.Records) ValidateValue(value.Value, replayed.Genesis.Options);
        }
        var restored = new RuntimeSession(task, replayed, Array.AsReadOnly(committedPrefix.ToArray()));
        if (restored.Options.JournalPath is not null)
        {
            if (File.Exists(restored.Options.JournalPath))
            {
                var disk = JournalStore.ReadCommitted(restored.Options.JournalPath, restored.Options.MaxJournalBytes);
                if (DtrCanonical.Digest(disk) != DtrCanonical.Digest(committedPrefix))
                { restored.Dispose(); throw new InvalidDataException("Recovery WAL differs from expected committed prefix."); }
            }
            JournalStore.WriteComplete(restored.Options.JournalPath, committedPrefix);
        }
        if (restored.state.Phase is RuntimePhase.Transporting or RuntimePhase.Prepared || restored.state.ProbeOpen)
        {
            var phase = restored.state.Phase;
            var preparedSuffix = committedPrefix.Reverse().TakeWhile(x => x.EventKind != "OUTPUT_PREPARE").ToArray();
            if (phase == RuntimePhase.Prepared && preparedSuffix.Any(x => x.EventKind is "PRECHECK_ACCEPT" or "FINAL_GATE_ALLOWANCE"))
            { restored.Dispose(); throw new InvalidDataException("Accepted fixed terminal tail cannot be unfrozen after interruption."); }
            restored.Append("RECOVERY_ABORT_PHASE", new { InterruptedPhase = phase.ToString(), Reason = "Explicit recovery of an interrupted unsealed phase" },
                restored.state with { Phase = phase == RuntimePhase.Prepared ? RuntimePhase.Idle : RuntimePhase.Committed, ProbeOpen = false, TransportSnapshotSeq = -1 }, 1);
            if (restored.state.Phase == RuntimePhase.Committed) restored.EndStep();
        }
        return restored;
    }

    public RuntimeSnapshot Snapshot()
    {
        lock (sync) return new(Options.SessionId, state.MicrostepId, state.TopologyRevision, state.ExactStateRevision,
            Array.AsReadOnly(state.Slots.ToArray()), Array.AsReadOnly(state.Edges.ToArray()),
            Array.AsReadOnly(state.Records.ToArray()), Array.AsReadOnly(state.Proofs.ToArray()),
            Options.MaxMicrosteps - state.StepsUsed, Options.MaxWork - state.WorkCost, state.Mode, journalBytes, journal.Count);
    }

    public bool BeginStep()
    {
        lock (sync)
        {
            Require(RuntimePhase.Idle);
            if (state.StepsUsed >= Options.MaxMicrosteps) return false;
            var next = state with
            {
                MicrostepId = checked(state.MicrostepId + 1), StepsUsed = checked(state.StepsUsed + 1),
                Phase = RuntimePhase.Editing, EdgeEditsThisStep = 0, SlotOperationsThisStep = 0,
                ControlReadsThisStep = 0, EditedEndpoints = [], ControlReadSet = [], TransportSnapshotSeq = -1
            };
            Append("STEP_BEGIN", new { Step = next.MicrostepId }, next, 1);
            transport = null;
            return true;
        }
    }

    public void RecordAudit(string kind, object payload, long cost)
    {
        lock (sync)
        {
            EnsureMutable();
            if (state.Phase is not (RuntimePhase.Idle or RuntimePhase.Editing)) throw new InvalidOperationException("Discovery metadata is frozen during transport/commit.");
            if (string.IsNullOrWhiteSpace(kind) || cost < 0) throw new ArgumentException("Invalid audit kind/cost.");
            var audit = new AuditPayload(kind, DtrCanonical.Json(payload));
            var next = state with { AuditDigest = DtrCanonical.Digest(new { Previous = state.AuditDigest, Audit = audit }) };
            Append("AUDIT", audit, next, cost);
        }
    }

    public IReadOnlyList<SlotHandle> Probe(IEnumerable<SlotHandle> handles)
    {
        lock (sync)
        {
            Require(RuntimePhase.Editing);
            var selected = handles.Distinct().OrderBy(x => x.SlotId).ThenBy(x => x.Generation).ToArray();
            if (state.ControlReadsThisStep + selected.Length > Options.MaxControlReadsPerStep) throw new RuntimeResourceException("Control-read quota exceeded.");
            foreach (var handle in selected) Live(handle);
            var payload = new ProbePayload(checked(state.ControlViewId + 1), selected);
            Append("CONTROL_PROBE_OPEN", payload, state with { ProbeOpen = true, ControlViewId = payload.ControlViewId }, 1);
            Append("CONTROL_PROBE_READ", payload, state with
            {
                ControlReadSet = state.ControlReadSet.Concat(selected).Distinct().OrderBy(x => x.SlotId).ThenBy(x => x.Generation).ToArray(),
                ControlReadsThisStep = checked(state.ControlReadsThisStep + selected.Length)
            }, Math.Max(1, selected.Length));
            Append("CONTROL_PROBE_CLOSE", payload, state with { ProbeOpen = false }, 1);
            return Array.AsReadOnly(selected);
        }
    }

    public TopologyResult ApplyTopology(TopologyTransaction transaction)
    {
        lock (sync)
        {
            Require(RuntimePhase.Editing);
            var copied = DtrCanonical.Parse<TopologyTransaction>(DtrCanonical.Json(transaction));
            var result = TopologyEngine.Evaluate(state, copied, Options);
            var payload = new TopologyPayload(copied, result.Result.Accepted, result.Result.Changed, result.Result.Reason, result.Result.AddedEdges);
            Append("TOPOLOGY_TRANSACTION", payload, result.State, checked(1 + copied.Edits.Count));
            return result.Result;
        }
    }

    public TransportFrame Transport()
    {
        lock (sync)
        {
            Require(RuntimePhase.Editing);
            var snapshotSeq = (long)journal.Count;
            Append("TRANSPORT_BEGIN", new { SnapshotSeq = snapshotSeq }, state with
                { Phase = RuntimePhase.Transporting, TransportSnapshotSeq = snapshotSeq }, 1);
            var reads = new List<ReadReceipt>(); var writes = new List<WriteReceipt>();
            foreach (var edge in state.Edges.OrderBy(x => x.EdgeId))
            {
                var source = Live(edge.Source); var target = Live(edge.Target);
                if (edge.Role == EdgeRole.WriteResult)
                {
                    var targetVersion = target.RecordId is null ? 0 : Record(target.RecordId.Value).ValueRevision;
                    var write = new WriteReceipt(Options.SessionId, state.MicrostepId, snapshotSeq,
                        source.Handle, target.Handle, targetVersion, edge.EdgeId, state.TopologyRevision, edge.Port,
                        source.BindingRevision, target.BindingRevision, edge.EdgeRevision, state.ControlViewId, journal.Count);
                    Append("WRITE_RECEIPT", write, state, 1); writes.Add(write);
                }
                else if (edge.Role != EdgeRole.NeuralMessage && source.RecordId is not null)
                {
                    var record = Record(source.RecordId.Value);
                    var read = new ReadReceipt(Options.SessionId, state.MicrostepId, snapshotSeq, record.RecordId,
                        record.ValueRevision, edge.EdgeId, state.TopologyRevision, source.Handle, target.Handle,
                        source.BindingRevision, target.BindingRevision, edge.EdgeRevision, edge.Port, state.ControlViewId, journal.Count);
                    Append("READ_RECEIPT", read, state, checked(1 + DtrCanonical.Encode(record.Value).LongLength / 64)); reads.Add(read);
                }
            }
            Append("TRANSPORT_END", new { SnapshotSeq = snapshotSeq, ReadCount = reads.Count, WriteCount = writes.Count },
                state with { Phase = RuntimePhase.Transported }, Math.Max(1, state.Edges.Length));
            transport = new(snapshotSeq, state.TopologyRevision, Array.AsReadOnly(reads.ToArray()), Array.AsReadOnly(writes.ToArray()));
            return transport;
        }
    }

    public CommitResult Commit(KernelAction proposed)
    {
        lock (sync)
        {
            Require(RuntimePhase.Transported);
            var action = DtrCanonical.Parse<KernelAction>(DtrCanonical.Json(proposed));
            var route = CheckRoute(action);
            KernelCheck check;
            var values = new List<DeliveredOperand>();
            if (!route.Accepted) check = KernelCheck.Reject(route.Reason);
            else
            {
                foreach (var receipt in action.ReadReceipts)
                {
                    var record = Record(receipt.SourceRecordId);
                    values.Add(new(record.Value, record.ProofId, record.InputId, receipt));
                }
                var view = new KernelReadView(values, action.WriteReceipt, Task);
                try
                {
                    if (action.Candidate is not null) ValidateValue(action.Candidate, Options);
                    check = BasicKernel.Supports(action.Operation) ? BasicKernel.Check(action, view, Options) : task.Check(action, view);
                    if (check.Accepted && action.Operation == "INPUT_REF" && !Task.Inputs.Any(x => x.InputId == check.SemanticInputId && x.Value == check.Output))
                        check = KernelCheck.Reject("Transported INPUT_REF value does not match its immutable input root.");
                }
                catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException or OverflowException)
                { check = KernelCheck.Reject("Task operation rejected: " + error.Message); }
            }
            if (check.Accepted && check.Output is not null)
            {
                try { ValidateValue(check.Output, Options); }
                catch (InvalidDataException error) { check = KernelCheck.Reject(error.Message); }
            }
            if (!check.Accepted || check.Output is null)
            {
                Append("SEMANTIC_REJECT", new SemanticPayload(action, false, check.Reason, null, null, null),
                    state with { Phase = RuntimePhase.Committed }, checked(1 + action.ReadReceipts.Count));
                return new(false, check.Reason);
            }
            var bytes = DtrCanonical.Encode(check.Output).LongLength;
            if (state.Proofs.LongLength >= Options.MaxProofs || bytes > Options.MaxExactBytes - state.ExactBytes)
                throw new RuntimeResourceException("Proof or exact-record capacity exceeded.");
            var destination = Live(action.WriteReceipt.Target);
            var proofId = (long)state.Proofs.Length; var recordId = (long)state.Records.Length;
            var target = destination with { RecordId = recordId, BindingRevision = checked(destination.BindingRevision + 1), MetadataRevision = checked(destination.MetadataRevision + 1) };
            var recordResult = new ExactRecord(recordId, check.SemanticInputId ?? string.Empty, check.Output, 1, proofId, false);
            var proof = new ProofEvent(proofId, Options.SessionId, Task.SemanticVersion, action.Operation,
                Array.AsReadOnly(values.Select(x => x.ProofId).ToArray()), Array.AsReadOnly(action.ReadReceipts.Select(x => x.ValueRevision).ToArray()),
                check.SemanticInputId, check.Output, 1, action.Worker, state.TopologyRevision,
                Array.AsReadOnly(action.ReadReceipts.ToArray()), action.WriteReceipt, target.Handle, 1,
                Array.AsReadOnly(state.ControlReadSet.ToArray()), state.MicrostepId, Options.ModelCheckpointDigest,
                InputDigest, state.Mode, journal.Count, state.TransportSnapshotSeq, target.BindingRevision, state.ControlViewId,
                action.Candidate);
            if (proof.InputProofIds.Any(x => x < 0 || x >= proofId)) throw new InvalidDataException("Evidence must reference strictly prior proofs.");
            var next = state with
            {
                Slots = state.Slots.Select(x => x.Handle == target.Handle ? target : x).ToArray(),
                Records = [..state.Records, recordResult], Proofs = [..state.Proofs, proof],
                ExactStateRevision = checked(state.ExactStateRevision + 1), ExactBytes = checked(state.ExactBytes + bytes),
                Phase = RuntimePhase.Committed
            };
            Append("SEMANTIC_COMMIT", new SemanticPayload(action, true, check.Reason, proof, recordResult, target), next,
                checked(1 + action.ReadReceipts.Count + bytes / 64));
            return new(true, check.Reason, proof, recordResult);
        }
    }

    public void EndStep()
    {
        lock (sync)
        {
            if (state.Phase is not (RuntimePhase.Editing or RuntimePhase.Transported or RuntimePhase.Committed)) throw new InvalidOperationException("Cannot end an incomplete transport or inactive step.");
            if (state.ProbeOpen) throw new InvalidOperationException("Control view remains open.");
            Append("STEP_END", new { Step = state.MicrostepId }, state with { Phase = RuntimePhase.Idle, TransportSnapshotSeq = -1 }, 1);
            transport = null;
        }
    }

    public void SetMode(ExecutionMode mode)
    {
        lock (sync)
        {
            Require(RuntimePhase.Idle);
            if (!Enum.IsDefined(mode) || state.Mode == ExecutionMode.FairFallback && mode == ExecutionMode.LearnedOnly
                || state.Mode == ExecutionMode.OracleDiagnostic && mode != ExecutionMode.OracleDiagnostic)
                throw new InvalidOperationException("Execution mode cannot conceal prior fallback or diagnostic intervention.");
            if (state.Mode == mode) return;
            Append("MODE_CHANGE", new { Before = state.Mode, After = mode }, state with { Mode = mode }, 1);
        }
    }

    /// <summary>Records failure and closes the current typed phase and step. False means no complete failure record could fit.</summary>
    public bool ReportFailure(string kind, string reason)
    {
        lock (sync)
        {
            if (disposed || state.Phase == RuntimePhase.Sealed) return false;
            if (string.IsNullOrWhiteSpace(kind) || kind.Length > 128 || reason is null || Encoding.UTF8.GetByteCount(reason) > Options.MaxValueBytes)
                throw new ArgumentException("Invalid bounded failure description.");
            if (state.Phase == RuntimePhase.Prepared && journal.AsEnumerable().Reverse().TakeWhile(x => x.EventKind != "OUTPUT_PREPARE")
                .Any(x => x.EventKind is "PRECHECK_ACCEPT" or "FINAL_GATE_ALLOWANCE")) return false;
            var hadStep = state.Phase is RuntimePhase.Editing or RuntimePhase.Transporting or RuntimePhase.Transported or RuntimePhase.Committed;
            try
            {
                var failurePayload = new FailurePayload(kind, reason, state.Phase.ToString());
                var closed = state with
                {
                    Phase = hadStep ? RuntimePhase.Committed : RuntimePhase.Idle,
                    ProbeOpen = false, TransportSnapshotSeq = -1
                };
                var failureCost = checked(1 + Encoding.UTF8.GetByteCount(reason) / 64);
                var preview = BuildEntry("FAILURE_CLOSE", failurePayload, state, closed, failureCost,
                    journal.Count, journal[^1].EventDigest);
                var requiredBytes = preview.Bytes;
                var finalWork = preview.State.WorkCost;
                var count = 1;
                if (hadStep)
                {
                    var end = BuildEntry("STEP_END", new { Step = state.MicrostepId }, preview.State,
                        preview.State with { Phase = RuntimePhase.Idle, TransportSnapshotSeq = -1 }, 1,
                        journal.Count + 1, preview.Event.EventDigest);
                    requiredBytes = checked(requiredBytes + end.Bytes); finalWork = end.State.WorkCost; count++;
                }
                if (journal.Count + count > Options.MaxJournalEvents || requiredBytes > Options.MaxJournalBytes - journalBytes || finalWork > Options.MaxWork)
                    return false;
                Append("FAILURE_CLOSE", failurePayload, closed, failureCost);
                transport = null;
                if (hadStep) EndStep();
                return true;
            }
            catch (RuntimeResourceException) { return false; }
        }
    }

    public RuntimeCertificate Seal(ExactValue output)
    {
        lock (sync)
        {
            Require(RuntimePhase.Idle);
            ValidateValue(output, Options);
            var root = DtrCanonical.Digest(state.Proofs);
            var sealedPayload = new SealPayload(output, root, state.Mode, Options.ModelCheckpointDigest, Options.FinalGateWorkAllowance);
            Append("OUTPUT_PREPARE", sealedPayload, state with { Phase = RuntimePhase.Prepared }, 1);
            var meter = new VerificationMeter(Math.Min(Options.FinalGateWorkAllowance, Options.MaxWork - state.WorkCost));
            SemanticVerification semantic;
            try
            {
                JournalReplay.Replay(journal, meter, requireSeal: false);
                Discovery.DirectoryAuditVerifier.Verify(journal);
                semantic = task.CreateIndependentVerifier().Verify(Task, state.Proofs, output, meter);
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException or InvalidOperationException or OverflowException or System.Text.Json.JsonException)
            { semantic = new(false, error.Message); }
            if (!semantic.Accepted || meter.Exhausted)
            {
                Append("PRECHECK_REJECT", new { semantic.Reason, UsedWork = meter.UsedWork }, state with { Phase = RuntimePhase.Idle }, meter.UsedWork + 1);
                throw new InvalidDataException("Independent pre-verification rejected output: " + semantic.Reason);
            }
            Append("PRECHECK_ACCEPT", new { semantic.Reason, UsedWork = meter.UsedWork }, state, meter.UsedWork + 1);
            Append("FINAL_GATE_ALLOWANCE", new { Allowance = Options.FinalGateWorkAllowance, MaxBytes = Options.MaxExportBytes }, state, Options.FinalGateWorkAllowance);
            Append("SEALED_EXPORT_CUT", sealedPayload, state with { Phase = RuntimePhase.Sealed }, 1);
            var tail = journal[^1];
            var anchor = new EndAnchor(Options.SessionId, journal[0].EventDigest, tail.EventSeq, tail.EventDigest,
                DtrCanonical.Digest(output), root, state.Mode, Options.ModelCheckpointDigest, Options.ExportAuthorityId,
                Guid.NewGuid().ToString("N"), Convert.ToBase64String(authority.ExportSubjectPublicKeyInfo()), string.Empty);
            anchor = anchor with { Signature = Convert.ToBase64String(authority.SignData(anchor.SigningBytes(), HashAlgorithmName.SHA256)) };
            TrustedEndAnchor = anchor;
            var manifest = new CertificateManifest("ConsDTRv1", "Draft-2", "ConsDTRv1-Replay-Draft-2", Options.SessionId,
                journal[0].EventDigest, InputDigest, "ConsDTRv1-Kernel-Draft-2", Task.SemanticVersion, Options.ModelCheckpointDigest,
                state.Mode, output, root, 0, tail.EventSeq, journal.Count, state.TopologyRevision,
                state.ExactStateRevision, tail.PostStateDigest, tail.EventDigest, state.WorkCost, Options.ExportAuthorityId, anchor.AnchorId);
            var certificate = new RuntimeCertificate(manifest, Task, Array.AsReadOnly(state.Proofs.ToArray()),
                Array.AsReadOnly(journal.ToArray()), anchor);
            if (DtrCanonical.Encode(certificate).LongLength > Options.MaxExportBytes
                || DtrCanonical.StrictUtf8.GetByteCount(certificate.Serialize()) > Options.MaxExportBytes)
                throw new RuntimeResourceException("Sealed certificate exceeds bounded canonical/interchange export size; no formal result.");
            var gate = IndependentVerifier.Verify(certificate, task.CreateIndependentVerifier(), anchor, Options.FinalGateWorkAllowance);
            if (!gate.Accepted) throw new InvalidDataException("Final output gate rejected sealed certificate: " + gate.Reason);
            return certificate;
        }
    }

    public void SaveJournal(string path) { lock (sync) JournalStore.WriteComplete(path, journal); }

    private void Append<T>(string kind, T payload, RuntimeState next, long operationCost)
    {
        EnsureMutable();
        var built = BuildEntry(kind, payload, state, next, operationCost, journal.Count,
            journal.Count == 0 ? string.Empty : journal[^1].EventDigest);
        if (built.State.WorkCost > Options.MaxWork) throw new RuntimeResourceException("Deterministic work budget exhausted.");
        if (journal.Count >= Options.MaxJournalEvents || built.Bytes > Options.MaxJournalBytes - journalBytes)
            throw new RuntimeResourceException("Journal capacity exhausted; state was not committed.");
        if (Options.JournalPath is not null) JournalStore.Append(Options.JournalPath, built.Event);
        journal.Add(built.Event); journalBytes = checked(journalBytes + built.Bytes); state = built.State;
    }

    private (JournalEvent Event, RuntimeState State, long Bytes) BuildEntry<T>(string kind, T payload,
        RuntimeState before, RuntimeState next, long operationCost, long sequence, string previousDigest)
    {
        if (operationCost < 0) throw new ArgumentOutOfRangeException(nameof(operationCost));
        var json = DtrCanonical.Json(payload);
        var delta = checked(operationCost + RuntimeAccounting.JournalWork(before, next, json));
        next = next with { WorkCost = checked(before.WorkCost + delta) };
        var entry = new JournalEvent(sequence, Options.SessionId, next.MicrostepId, next.Phase.ToString(), kind,
            json, before.TopologyRevision, next.TopologyRevision, before.ExactStateRevision, next.ExactStateRevision,
            before.Digest(), next.Digest(), previousDigest, string.Empty, delta, next.WorkCost, next.Mode, operationCost);
        entry = entry with { EventDigest = entry.ComputeDigest() };
        return (entry, next, DtrCanonical.Encode(entry).LongLength);
    }

    private KernelCheck CheckRoute(KernelAction action)
    {
        if (transport is null || action.ReadReceipts.Count > Options.MaxInDegree) return KernelCheck.Reject("No current bounded inbox.");
        if (action.ReadReceipts.Select(x => x.ReceiptJournalSeq).Distinct().Count() != action.ReadReceipts.Count) return KernelCheck.Reject("Repeated operand receipt.");
        if (!state.Slots.Any(x => x.Handle == action.Worker && x.Active && x.Kind == SlotKind.Worker)) return KernelCheck.Reject("Invalid worker identity.");
        foreach (var read in action.ReadReceipts)
        {
            if (read.Receiver != action.Worker || !transport.ReadReceipts.Contains(read)) return KernelCheck.Reject("Operand was not transported to this worker in the current snapshot.");
            var source = state.Slots.SingleOrDefault(x => x.Handle == read.Source);
            var receiver = state.Slots.SingleOrDefault(x => x.Handle == read.Receiver);
            var record = state.Records.SingleOrDefault(x => x.RecordId == read.SourceRecordId);
            var edge = state.Edges.SingleOrDefault(x => x.EdgeId == read.EdgeId);
            if (source is null || receiver is null || record is null || edge is null || !source.Active || !receiver.Active
                || source.RecordId != record.RecordId || record.ValueRevision != read.ValueRevision
                || source.BindingRevision != read.SourceBindingRevision || receiver.BindingRevision != read.ReceiverBindingRevision
                || edge.Source != read.Source || edge.Target != read.Receiver || edge.EdgeRevision != read.EdgeRevision || edge.Port != read.Port
                || read.SessionId != Options.SessionId || read.MicrostepId != state.MicrostepId || read.TopologyRevision != state.TopologyRevision
                || read.ControlViewId != state.ControlViewId || read.TransportSnapshotSeq != state.TransportSnapshotSeq)
                return KernelCheck.Reject("Stale, substituted or cross-session read receipt.");
        }
        var write = action.WriteReceipt;
        if (write.Worker != action.Worker || !transport.WriteReceipts.Contains(write)) return KernelCheck.Reject("Missing current WRITE_RESULT authorization.");
        var target = state.Slots.SingleOrDefault(x => x.Handle == write.Target);
        var worker = state.Slots.Single(x => x.Handle == action.Worker);
        var writeEdge = state.Edges.SingleOrDefault(x => x.EdgeId == write.EdgeId);
        if (target is null || !target.Active || target.Kind != SlotKind.Register || target.RecordId is not null
            || target.BindingRevision != write.TargetBindingRevision || worker.BindingRevision != write.WorkerBindingRevision
            || write.TargetRevisionBeforeWrite != 0 || writeEdge is null || writeEdge.Role != EdgeRole.WriteResult
            || writeEdge.Source != worker.Handle || writeEdge.Target != target.Handle || writeEdge.EdgeRevision != write.EdgeRevision || writeEdge.Port != write.WritePort
            || write.SessionId != Options.SessionId || write.MicrostepId != state.MicrostepId || write.TopologyRevision != state.TopologyRevision
            || write.ControlViewId != state.ControlViewId || write.TransportSnapshotSeq != state.TransportSnapshotSeq)
            return KernelCheck.Reject("Write target occupied, changed, stale or unauthorized.");
        return new(true, "Route accepted");
    }

    private SlotState Live(SlotHandle handle) => state.Slots.SingleOrDefault(x => x.Handle == handle && x.Active)
        ?? throw new InvalidDataException("Stale or inactive slot handle.");
    private ExactRecord Record(long id) => state.Records.SingleOrDefault(x => x.RecordId == id)
        ?? throw new InvalidDataException("Unknown exact record identity.");
    private void Require(RuntimePhase phase)
    {
        EnsureMutable();
        if (state.Phase != phase || state.ProbeOpen) throw new InvalidOperationException("Operation is illegal in the current phase: " + state.Phase);
    }
    private void EnsureMutable()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (state.Phase == RuntimePhase.Sealed) throw new InvalidOperationException("Session is permanently sealed.");
    }
    internal static void ValidateValue(ExactValue value, SessionOptions options)
    {
        if (string.IsNullOrWhiteSpace(value.Type) || value.Type.Length > 128 || value.Data is null || !Enum.IsDefined(value.Trust))
            throw new InvalidDataException("Invalid exact value type/trust.");
        if (Encoding.UTF8.GetByteCount(value.Data) > options.MaxValueBytes) throw new InvalidDataException("Exact value length exceeded.");
        if (value.Type == "bool" && value.Data is not ("true" or "false")) throw new InvalidDataException("Noncanonical exact boolean.");
        if (value.Type == "int")
        {
            if (!System.Numerics.BigInteger.TryParse(value.Data, System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out var integer) || integer.ToString(System.Globalization.CultureInfo.InvariantCulture) != value.Data
                || integer < -(System.Numerics.BigInteger.One << (options.MaxIntegerBits - 1)) || integer >= (System.Numerics.BigInteger.One << (options.MaxIntegerBits - 1)))
                throw new InvalidDataException("Noncanonical or out-of-width exact integer.");
        }
    }
    private static void ValidateTask(TaskDefinition definition, SessionOptions options)
    {
        if (definition.Inputs is null || string.IsNullOrEmpty(definition.TaskId) || string.IsNullOrEmpty(definition.SemanticVersion)
            || definition.Inputs.Select(x => x.InputId).Distinct(StringComparer.Ordinal).Count() != definition.Inputs.Count)
            throw new InvalidDataException("Invalid task/schema or repeated input identity.");
        if (definition.CanonicalInput is null || definition.CanonicalQuery is null
            || (long)Encoding.UTF8.GetByteCount(definition.CanonicalInput) + Encoding.UTF8.GetByteCount(definition.CanonicalQuery) > options.MaxExactBytes)
            throw new RuntimeResourceException("Normalized input/query exceeds exact input capacity.");
        foreach (var input in definition.Inputs)
        {
            ValidateValue(input.Value, options);
            if (string.IsNullOrEmpty(input.InputId) || input.Value.Trust != ValueTrust.Verified) throw new InvalidDataException("Input roots must have explicit identities and trusted input status.");
        }
    }
    internal static void ValidateOptions(SessionOptions options)
    {
        if (options.SessionId == Guid.Empty || !Enum.IsDefined(options.Mode) || options.WorkerCount < 1 || options.RegisterCount < 1
            || options.MaxSlots < 1 || options.MaxInDegree < 1 || options.MaxOutDegree < 1 || options.MaxEdgeEditsPerStep < 1
            || options.MaxSlotOperationsPerStep < 0 || options.MaxEditedEndpointsPerStep < 2 || options.MaxControlReadsPerStep < 0
            || options.MaxMicrosteps < 0 || options.MaxWork < 1 || options.MaxJournalEvents < 1 || options.MaxJournalBytes < 1
            || options.MaxProofs < 1 || options.MaxExactBytes < 1 || options.MaxValueBytes < 1 || options.MaxIntegerBits < 2
            || options.MaxIntegerBits > 65_536 || options.FinalGateWorkAllowance < 1 || options.MaxExportBytes < 1
            || string.IsNullOrWhiteSpace(options.ExportAuthorityId) || options.ExportAuthorityId.Length > 128
            || string.IsNullOrWhiteSpace(options.ModelCheckpointDigest) || options.ModelCheckpointDigest.Length > 256)
            throw new ArgumentException("Invalid finite session resource contract.");
    }
    public void Dispose() { lock (sync) { if (disposed) return; authority.Dispose(); disposed = true; } }
}
