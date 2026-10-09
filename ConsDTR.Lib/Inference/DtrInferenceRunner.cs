using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ConsDTR.Lib.Models;
using ConsDTR.Lib.Training;
using ConsDTR.Runtime;
using ConsDTR.Runtime.Discovery;
using ConsDTR.Runtime.Tasks;
using TorchSharp;
using static TorchSharp.torch;

namespace ConsDTR.Lib.Inference;

/// <summary>Frozen-parameter, metered hard-topology inference. Only the independent gate certifies answers.</summary>
public sealed class DtrInferenceRunner(DtrNeuralModel model)
{
    public DtrInferenceResult Run(IKernelTask task, DtrInferenceOptions? options = null,
        DtrInferenceResume? resume = null, Func<DtrInferenceCheckpointContext, bool>? checkpoint = null)
        => RunCore(task, options, resume, checkpoint, null);

    internal DtrInferenceResult RunCore(IKernelTask task, DtrInferenceOptions? options,
        DtrInferenceResume? resume, Func<DtrInferenceCheckpointContext, bool>? checkpoint, DtrHardRolloutCollector? training,
        Func<string, DtrPolicyField, IReadOnlyList<int>, int?>? teacherProvider = null,
        Action<DtrTeacherChoiceContext>? teacherContext = null)
    {
        options ??= resume?.Options ?? new();
        if (resume is not null)
        {
            if (resume.Options.Directory != options.Directory) throw new ArgumentException("Resume must retain its directory contract.");
            options = options with { Runtime = resume.Runtime.Options };
        }
        options = options with { Runtime = options.Runtime with { ModelCheckpointDigest = DtrCheckpointStore.ComputeModelDigest(model) } };
        if (options.Runtime.Mode != ExecutionMode.LearnedOnly && teacherProvider is null)
            throw new ArgumentException("Use the explicit task-plan executor for fallback or diagnostic runs.");
        options.Directory.Validate();
        if (options.Runtime.MaxInDegree > model.Config.PortClasses || options.Runtime.MaxOutDegree > model.Config.PortClasses)
            throw new ArgumentException("Port head capacity must cover the declared bounded inbox/write degree.");
        if (DtrActionVocabulary.ForTask(task).Count > model.Config.OperationClasses || model.Config.RelationTypes < Enum.GetValues<EdgeRole>().Length)
            throw new ArgumentException("The shared operation and relation heads must cover the declared task/runtime vocabulary.");
        if (training is null) model.FreezeForInference();
        else model.BeginTraining();
        using var noGradient = training is null ? no_grad() : null;
        using var runtime = resume?.Runtime ?? RuntimeSession.Create(task, options.Runtime);
        var clock = Stopwatch.StartNew();
        var directory = resume?.Directory ?? new CandidateDirectory(options.Directory, runtime.RecordAudit);
        var intent = resume?.Intent ?? new IntentState(options.Directory.MaxIntentSteps, runtime.RecordAudit);
        var random = training?.Random ?? resume?.ActionRandom ?? new DtrRandomStream(options.SamplingSeed);
        var policy = new DtrDiscretePolicy(model, random, options.Deterministic, options.Exploration, teacherProvider);
        var editorRandom = resume?.EditorRandom ?? new DtrRandomStream(options.SamplingSeed ^ 0x9e3779b97f4a7c15UL);
        var controller = new DtrNeuralController(model, policy, editorRandom, teacherContext);
        Tensor? hidden = resume?.Hidden.clone().DetachFromDisposeScope();
        IReadOnlyList<SlotState>? priorSlots = resume?.NeuralSlots;
        long rejects = 0;
        long edits = 0;
        try
        {
            runtime.RecordAudit("INFERENCE_CONFIGURATION", new
            {
                options.Editor, options.Messages, options.Deterministic,
                Seed = options.SamplingSeed.ToString(CultureInfo.InvariantCulture),
                Exploration = options.Exploration.ToString("R", CultureInfo.InvariantCulture),
                ParameterCount = model.ParameterCount, CandidateTargetMode = "ALL_ACTIVE_SLOTS",
                NeuralBackend = "SparseEdgeTable", NeuralWorkAccounting = "ConservativeFiniteOperationAllowance"
            }, 1);
            if (teacherProvider is not null) runtime.RecordAudit("TRAINING_SCAFFOLD", new { Course = "B", ProductionController = true, Teacher = "ExplicitProvider" }, 1);
            using var initialScope = NewDisposeScope();
            var goalBytes = Encoding.UTF8.GetByteCount(task.Definition.CanonicalQuery);
            var goalAllowance = checked(64L * (2L * model.Config.FeatureDim * model.Config.RelationRank +
                2L * model.Config.RelationRank * model.Config.RelationRank));
            runtime.RecordAudit("GOAL_ENCODING", new { Bytes = goalBytes, TensorScalarAllowance = goalAllowance }, checked(goalBytes + 1 + (goalAllowance + 1023) / 1024));
            var goal = model.EncodeGoal(DtrFeatureEncoder.EncodeGoal(task.Definition, model.Config, model.Device)).DetachFromDisposeScope();
            try
            {
                while (runtime.BeginStep())
                {
                    using var scope = NewDisposeScope();
                    var snapshot = runtime.Snapshot();
                    var trace = training?.BeginStep(snapshot) ?? new DtrPolicyTrace();
                    using var inferenceTrace = training is null ? trace : null;
                    var maintenanceBytes = snapshot.Records.Sum(r => (long)Encoding.UTF8.GetByteCount(r.Value.Data));
                    runtime.RecordAudit("DIRECTORY_MAINTENANCE_ALLOWANCE", new { Slots = snapshot.Slots.Count, Edges = snapshot.Edges.Count, Bytes = maintenanceBytes },
                        checked(snapshot.Slots.Count + snapshot.Edges.Count * (long)options.Directory.MaxTargetsPerStep + maintenanceBytes + 1));
                    SynchronizeDirectory(directory, snapshot);
                    if (intent.Current is { } oldIntent && (snapshot.MicrostepId >= oldIntent.ExpiresAt || oldIntent.Anchors.Any(hd => !directory.IsLive(hd))))
                    {
                        directory.AbortPins(oldIntent.Worker);
                        intent.Abort("EXPIRED_OR_STALE");
                    }
                    if (directory.Report.Complete)
                        directory.BeginEpoch(_ => ["Observation", "Register", "Worker", "Goal"], snapshot.RemainingMicrosteps);
                    var publicWorker = snapshot.Slots.FirstOrDefault(s => s.Active && s.Kind == SlotKind.Worker);
                    var recoveringWorkers = publicWorker is null;
                    // A public active control endpoint may reactivate an exposed worker. It cannot execute semantic actions.
                    publicWorker ??= snapshot.Slots.FirstOrDefault(s => s.Active);
                    if (publicWorker is null) { runtime.EndStep(); return Finish(DtrResultStatus.Unknown, "NO_ACTIVE_CONTROL_ENDPOINT"); }
                    var targets = intent.Current is null ? new[] { checked((int)publicWorker.Handle.SlotId) } : new[] { intent.Current.Worker };
                    var keys = intent.Current?.Anchors.Select(h => h.ObjectId).ToArray();
                    var pages = directory.NextPages(snapshot.MicrostepId, targets, keys, id =>
                        snapshot.Edges.Where(e => e.Target.SlotId == id || e.Source.SlotId == id)
                            .Select(e => checked((int)(e.Target.SlotId == id ? e.Source.SlotId : e.Target.SlotId))).Distinct().ToArray());
                    var headers = pages.SelectMany(p => p.Candidates.Append(p.Target)).Where(directory.IsLive).Distinct().ToArray();
                    training?.SetCandidates(headers);
                    var readHandles = headers.Where(hd => hd.Active).Select(hd => new SlotHandle(hd.Slot, hd.Generation)).Distinct().ToArray();
                    runtime.Probe(readHandles);
                    DtrNeuralWorkAllowance.Charge(runtime, snapshot, model.Config, options.Directory, true,
                        headers.Length, pages.Sum(page => page.Candidates.Count));
                    using var encoded = DtrFeatureEncoder.EncodeSnapshot(snapshot, model.Config, model.Device);
                    var local = model.EncodeLocal(encoded.Features);
                    var h = DtrFeatureEncoder.SynchronizeHidden(model, priorSlots, hidden, encoded, local);
                    if (training is not null)
                    {
                        var baselineContext = readHandles.Length == 0 ? zeros([1, model.Config.HiddenDim], device: model.Device) :
                            model.CreatePolicyContext(h, local, goal, encoded.SlotIndices[readHandles[0].SlotId], DtrNeuralController.Budget(snapshot));
                        training.SetValue(model.Value(baselineContext));
                    }
                    var workerSupport = readHandles.Where(handle => snapshot.Slots.Any(s => s.Handle == handle && s.Active && s.Kind == SlotKind.Worker)).ToArray();
                    if (workerSupport.Length == 0 && recoveringWorkers && readHandles.Length > 0) workerSupport = [readHandles[0]];
                    if (workerSupport.Length == 0)
                    {
                        runtime.ApplyTopology(new([]));
                        runtime.Transport();
                        var idleEdges = MessageEdges(snapshot, encoded, options.Messages);
                        var nextIdle = model.ForwardAlongEdges(h, local, goal, idleEdges.Sources,
                            idleEdges.Targets, idleEdges.Relations,
                            encoded.Slots.Select(s => s.Active).ToArray());
                        hidden?.Dispose(); hidden = nextIdle.DetachFromDisposeScope(); priorSlots = encoded.Slots;
                        runtime.EndStep();
                        var idleBoundary = training?.IsBpttBoundary(snapshot.MicrostepId) == true;
                        if (idleBoundary) { var detached = hidden.detach().DetachFromDisposeScope(); hidden.Dispose(); hidden = detached; }
                        training?.EndStep(runtime.Snapshot(), idleBoundary);
                        if (checkpoint?.Invoke(new(runtime, model, hidden, priorSlots, directory, intent, random, editorRandom, options)) == true)
                            return Finish(DtrResultStatus.Unknown, "CHECKPOINTED_FOR_EXCLUSIVE_RESUME");
                        continue;
                    }
                    publicWorker = snapshot.Slots.Single(s => s.Handle == workerSupport[0]);
                    var seedContext = model.CreatePolicyContext(h, local, goal, encoded.SlotIndices[publicWorker.Handle.SlotId], DtrNeuralController.Budget(snapshot));
                    if (training is not null) training.SetValue(model.Value(seedContext));
                    teacherContext?.Invoke(new("intent.worker", snapshot, SlotChoices: workerSupport));
                    var chooseWorker = policy.Choose(model.PointerLogits(seedContext, h, local,
                        workerSupport.Select(w => encoded.SlotIndices[w.SlotId]).ToArray()), DtrPolicyField.Pointer, "intent.worker");
                    trace.Add(chooseWorker);
                    var worker = workerSupport[chooseWorker.Choice];
                    var context = model.CreatePolicyContext(h, local, goal, encoded.SlotIndices[worker.SlotId], DtrNeuralController.Budget(snapshot));
                    teacherContext?.Invoke(new("intent.lifecycle", snapshot));
                    var decision = policy.ChooseField(context, DtrPolicyField.Intent, "intent.lifecycle", intent.Current is null ? [0, 2] : [1, 2]);
                    trace.Add(decision);
                    context = model.AdvancePolicyContext(context, DtrPolicyField.Intent, decision.Choice);
                    if (decision.Choice == 2)
                    {
                        if (intent.Current is not null) directory.AbortPins(intent.Current.Worker);
                        intent.Abort("POLICY_ABORT");
                    }
                    else if (intent.Current is not null) intent.Continue(snapshot.MicrostepId, "ROUTE", directory.IsLive);
                    else
                    {
                        var anchors = pages.SelectMany(p => p.Candidates).Where(hd => hd.Active && directory.IsLive(hd))
                            .Distinct().Where(hd => hd.Slot != worker.SlotId).ToArray();
                        if (anchors.Length > 0)
                        {
                            var families = DtrActionVocabulary.ForTask(task);
                            teacherContext?.Invoke(new("intent.operation-family", snapshot, Operations: families));
                            var family = policy.ChooseField(context, DtrPolicyField.Operation, "intent.operation-family", Enumerable.Range(0, families.Count).ToArray());
                            trace.Add(family);
                            context = model.AdvancePolicyContext(context, DtrPolicyField.Operation, family.Choice);
                            teacherContext?.Invoke(new("intent.anchor", snapshot, SlotChoices: anchors.Select(hd => new SlotHandle(hd.Slot, hd.Generation)).ToArray()));
                            var pointer = policy.Choose(model.PointerLogits(context, h, local,
                                anchors.Select(hd => encoded.SlotIndices[hd.Slot]).ToArray()), DtrPolicyField.Pointer, "intent.anchor");
                            trace.Add(pointer);
                            context = model.AdvancePolicyContext(context, DtrPolicyField.Pointer, pointer.Choice);
                            var anchor = anchors[pointer.Choice];
                            var workerHeader = headers.Single(hd => hd.Slot == worker.SlotId);
                            var intentAnchors = new List<CandidateHeader> { workerHeader, anchor };
                            var writeCandidates = anchors.Where(hd => snapshot.Slots.Any(slot => slot.Handle.SlotId == hd.Slot && slot.Kind == SlotKind.Register && slot.RecordId is null)).ToArray();
                            var writeTarget = anchor.Slot;
                            if (writeCandidates.Length > 0)
                            {
                                teacherContext?.Invoke(new("intent.write-target", snapshot, SlotChoices: writeCandidates.Select(hd => new SlotHandle(hd.Slot, hd.Generation)).ToArray()));
                                var writeChoice = policy.Choose(model.PointerLogits(context, h, local,
                                    writeCandidates.Select(hd => encoded.SlotIndices[hd.Slot]).ToArray()), DtrPolicyField.Pointer, "intent.write-target");
                                trace.Add(writeChoice);
                                context = model.AdvancePolicyContext(context, DtrPolicyField.Pointer, writeChoice.Choice);
                                var writeHeader = writeCandidates[writeChoice.Choice];
                                if (!intentAnchors.Contains(writeHeader)) intentAnchors.Add(writeHeader);
                                writeTarget = writeHeader.Slot;
                                directory.PinExposed(checked((int)worker.SlotId), writeHeader, snapshot.MicrostepId, checked(snapshot.MicrostepId + options.Directory.MaxIntentSteps));
                            }
                            intent.Begin(snapshot.MicrostepId, checked((int)worker.SlotId), families[family.Choice], intentAnchors, writeTarget, "write");
                            directory.PinExposed(checked((int)worker.SlotId), anchor, snapshot.MicrostepId, intent.Current!.ExpiresAt);
                        }
                    }
                    var proposed = controller.DecodeEdits(snapshot, pages, encoded, h, local, goal, worker, options.Runtime, trace, options.Editor, readHandles, context);
                    var transaction = runtime.ApplyTopology(proposed);
                    training?.SetEdits(transaction.Accepted ? proposed.Edits.Count(e => e.Kind is not TopologyEditKind.NoOp) : 0);
                    if (transaction.Accepted) edits += proposed.Edits.Count(e => e.Kind is not TopologyEditKind.NoOp);
                    else rejects++;
                    var frame = runtime.Transport();
                    var after = runtime.Snapshot();
                    using var postEncoded = DtrFeatureEncoder.EncodeSnapshot(after, model.Config, model.Device);
                    var postLocal = model.EncodeLocal(postEncoded.Features);
                    var matched = DtrFeatureEncoder.SynchronizeHidden(model, encoded.Slots, h, postEncoded, postLocal);
                    var messageEdges = MessageEdges(after, postEncoded, options.Messages);
                    var next = model.ForwardAlongEdges(matched, postLocal, goal, messageEdges.Sources, messageEdges.Targets,
                        messageEdges.Relations, postEncoded.Slots.Select(s => s.Active).ToArray());
                    DtrNeuralModel.EnsureFinite(next, "post-message state");
                    hidden?.Dispose();
                    hidden = next.DetachFromDisposeScope();
                    priorSlots = postEncoded.Slots;
                    CommitResult? committed = null;
                    var stop = false;
                    if (after.Slots.Any(s => s.Active && s.Kind == SlotKind.Worker && s.Handle == worker))
                    {
                        var decoded = controller.DecodeAction(task, after, frame, postEncoded, hidden, postLocal, goal, worker, trace,
                            (operation, ctx, tr, selected) => GenerateCandidate(task, operation, ctx, tr, policy,
                                headers.Where(hd => after.Slots.Any(s => s.Handle.SlotId == hd.Slot && s.Handle.Generation == hd.Generation &&
                                    s.Active && s.BindingRevision == hd.BindingRevision && s.MetadataRevision == hd.MetadataRevision)).ToArray(),
                                postEncoded, hidden, postLocal, selected, after, teacherContext));
                        stop = decoded.Stop;
                        if (decoded.Action is not null)
                        {
                            committed = runtime.Commit(decoded.Action);
                            training?.SetCommit(committed.Accepted);
                            if (!committed.Accepted) rejects++;
                        }
                    }
                    runtime.EndStep();
                    var bpttBoundary = training?.IsBpttBoundary(snapshot.MicrostepId) == true;
                    if (bpttBoundary)
                    {
                        var detached = hidden.detach().DetachFromDisposeScope();
                        hidden.Dispose(); hidden = detached;
                    }
                    training?.EndStep(runtime.Snapshot(), bpttBoundary);
                    if (checkpoint?.Invoke(new(runtime, model, hidden, priorSlots, directory, intent, random, editorRandom, options)) == true)
                        return Finish(DtrResultStatus.Unknown, "CHECKPOINTED_FOR_EXCLUSIVE_RESUME");
                    if (stop && committed?.Record is not null)
                    {
                        try
                        {
                            var certificate = runtime.Seal(committed.Record.Value);
                            var report = IndependentVerifier.Verify(certificate, task.CreateIndependentVerifier(), runtime.TrustedEndAnchor!, options.Runtime.FinalGateWorkAllowance);
                            return Finish(report.IsNegative ? DtrResultStatus.VerifiedNegative : DtrResultStatus.VerifiedResult,
                                report.Reason, certificate, report);
                        }
                        catch (InvalidDataException) { rejects++; }
                    }
                }
                return Finish(DtrResultStatus.Unknown, "MICROSTEP_BUDGET_EXHAUSTED");
            }
            finally { goal.Dispose(); }
        }
        catch (DtrNumericException error) { CloseFailure("NUMERIC_FAILURE", error.Message); return Finish(DtrResultStatus.ResourceOrNumericFailure, error.Message); }
        catch (RuntimeResourceException error) { CloseFailure("RESOURCE_FAILURE", error.Message); return Finish(DtrResultStatus.ResourceOrNumericFailure, error.Message); }
        catch (InvalidOperationException error) when (error.Message.Contains("EXPOSURE_", StringComparison.Ordinal))
        { CloseFailure("EXPOSURE_FAILURE", error.Message); return Finish(DtrResultStatus.Unknown, error.Message); }
        finally { hidden?.Dispose(); }

        DtrInferenceResult Finish(DtrResultStatus status, string reason, RuntimeCertificate? certificate = null, VerificationReport? verification = null)
        {
            var result = new DtrInferenceResult(status, reason, runtime.Options.Mode, runtime.Snapshot(), certificate, verification, directory.Report, rejects, edits,
                clock.Elapsed.TotalMilliseconds, runtime.TrustedEndAnchor, runtime.Journal);
            training?.Finish(result);
            return result;
        }
        void CloseFailure(string kind, string reason)
        {
            runtime.ReportFailure(kind, reason);
        }
    }

    internal static void ChargeNeuralStep(RuntimeSession runtime, RuntimeSnapshot snapshot, DtrNeuralModel model,
        DirectoryOptions? directory = null, bool includePolicy = true) =>
        DtrNeuralWorkAllowance.Charge(runtime, snapshot, model.Config, directory ?? new(), includePolicy);

    private static (int[] Sources, int[] Targets, int[] Relations) MessageEdges(RuntimeSnapshot snapshot, DtrEncodedState encoded, DtrMessageMode mode)
    {
        if (mode == DtrMessageMode.Disabled) return ([], [], []);
        var edges = snapshot.Edges.ToArray();
        var sources = edges.Select(edge => encoded.SlotIndices[edge.Source.SlotId]).ToArray();
        // Exogenous deterministic permutation affects only neural messages. Exact transport retains its real edges.
        if (mode == DtrMessageMode.Shuffled && sources.Length > 1)
        {
            var slots = snapshot.Slots.ToDictionary(slot => slot.Handle.SlotId);
            foreach (var group in edges.Select((edge, index) => (edge, index)).GroupBy(item =>
                (item.edge.Role, SourceKind: slots[item.edge.Source.SlotId].Kind, TargetKind: slots[item.edge.Target.SlotId].Kind)))
            {
                var indices = group.Select(item => item.index).ToArray();
                var original = indices.Select(index => sources[index]).ToArray();
                for (var i = 0; i < indices.Length; i++) sources[indices[i]] = original[(i + 1) % indices.Length];
            }
        }
        return (sources, edges.Select(edge => encoded.SlotIndices[edge.Target.SlotId]).ToArray(), edges.Select(edge => (int)edge.Role).ToArray());
    }

    private static void SynchronizeDirectory(CandidateDirectory directory, RuntimeSnapshot snapshot)
    {
        var records = snapshot.Records.ToDictionary(r => r.RecordId);
        foreach (var slot in snapshot.Slots)
        {
            var label = slot.RecordId is { } id ? records[id].InputId : string.Empty;
            if (slot.RecordId is { } atomId && records[atomId].Value.Type == "horn.atom")
                label = "fact:" + JsonSerializer.Deserialize<string>(records[atomId].Value.Data);
            if (string.IsNullOrEmpty(label)) label = $"slot:{slot.Handle.SlotId}";
            directory.Upsert(new(checked((int)slot.Handle.SlotId), slot.Handle.Generation, slot.BindingRevision,
                slot.MetadataRevision, label, slot.Kind.ToString(), IsTarget: slot.Active, Active: slot.Active));
        }
    }

    private static ExactValue? GenerateCandidate(IKernelTask task, string operation, Tensor context,
        DtrPolicyTrace trace, DtrDiscretePolicy policy, IReadOnlyList<CandidateHeader> headers,
        DtrEncodedState encoded, Tensor hidden, Tensor local, IReadOnlyList<ReadReceipt> receipts, RuntimeSnapshot snapshot,
        Action<DtrTeacherChoiceContext>? teacherContext)
    {
        if (operation == "PROPOSE_VALUE")
        {
            teacherContext?.Invoke(new("candidate.integer", snapshot, ReadChoices: receipts));
            return new("int", policy.DecodeInt64(context, trace).ToString(CultureInfo.InvariantCulture), ValueTrust.Candidate);
        }
        if (operation is not (HornTask.ProposeClosedSet or HornTask.ExtendClosedSet or ReachabilityTask.ProposeCut or
            ReachabilityTask.ExtendCut or FiniteCspTask.ProposeAssignment or FiniteCspTask.ExtendAssignment)) return null;
        var extending = operation is HornTask.ExtendClosedSet or ReachabilityTask.ExtendCut or FiniteCspTask.ExtendAssignment;
        long nextRevision = 1;
        if (extending)
        {
            // Revision headers come only from the selected current inbox, never an arbitrary global record lookup.
            var candidateType = task switch { HornTask => "horn.set", ReachabilityTask => "path.cut", _ => "csp.assignment" };
            var previous = receipts.Select(receipt => snapshot.Records.Single(record => record.RecordId == receipt.SourceRecordId))
                .FirstOrDefault(record => record.Value.Type == candidateType && record.Value.Trust == ValueTrust.Candidate);
            if (previous is null) return null; // The kernel records the concrete missing-operand rejection.
            nextRevision = checked(TaskCandidates.RevisionOf(previous.Value) + 1);
        }
        teacherContext?.Invoke(new("sequence.length", snapshot, SlotChoices: headers.Select(h => new SlotHandle(h.Slot, h.Generation)).ToArray(), ReadChoices: receipts));
        var selected = policy.DecodeSequence(context, hidden, local, headers.Select(h => encoded.SlotIndices[h.Slot]).Distinct().ToArray(), trace,
            Math.Min(headers.Count, modelSequenceLimit(policy)), withoutReplacement: true,
            pointerSupport: (name, support) => teacherContext?.Invoke(new(name, snapshot,
                SlotChoices: support.Select(index => encoded.Slots[index].Handle).ToArray(), ReadChoices: receipts)));
        var labels = selected.Select(i => headers.First(h => encoded.SlotIndices[h.Slot] == i).ObjectId).ToArray();
        return task switch
        {
            HornTask horn => extending ? TaskCandidates.HornClosedSetDelta(horn.Problem, HornMembers(), nextRevision) :
                TaskCandidates.HornClosedSet(horn.Problem, HornMembers()),
            ReachabilityTask path => extending ? TaskCandidates.GraphCutDelta(path.Problem, Vertices(), nextRevision) :
                TaskCandidates.GraphCut(path.Problem, Vertices()),
            FiniteCspTask csp => extending ? TaskCandidates.AssignmentDelta(csp.Problem, Assignments(csp), nextRevision) :
                TaskCandidates.Assignment(csp.Problem, Assignments(csp)),
            _ => null
        };
        IEnumerable<string> HornMembers() => labels.Where(l => l.StartsWith("fact:", StringComparison.Ordinal) || l.StartsWith("atom:", StringComparison.Ordinal)).Select(l => l[5..]);
        IEnumerable<string> Vertices() => labels.Where(l => l.StartsWith("vertex:", StringComparison.Ordinal)).Select(l => l[7..]);
        IEnumerable<KeyValuePair<string, int>> Assignments(FiniteCspTask csp)
        {
            var assignmentContext = context;
            foreach (var label in labels.Where(l => l.StartsWith("variable:", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal))
            {
                var variable = csp.Problem.Variables.Single(v => v.Id == label[9..]);
                var header = headers.First(h => h.ObjectId == label);
                teacherContext?.Invoke(new("candidate.assignment.integer", snapshot, SlotChoices: [new SlotHandle(header.Slot, header.Generation)], ReadChoices: receipts));
                // Each value is conditioned on its selected variable identity and the earlier assignment prefix.
                assignmentContext = modelContext(policy, assignmentContext, encoded.SlotIndices[header.Slot]);
                var value = (int)(unchecked((ulong)policy.DecodeInt64(assignmentContext, trace)) % (ulong)variable.Domain.Length);
                yield return new(variable.Id, variable.Domain[value]);
                assignmentContext = modelContext(policy, assignmentContext, value);
            }
        }
        static int modelSequenceLimit(DtrDiscretePolicy value) => value.MaximumSequenceLength;
        static Tensor modelContext(DtrDiscretePolicy value, Tensor ctx, int choice) => value.AdvanceContext(ctx, DtrPolicyField.Pointer, choice);
    }
}
