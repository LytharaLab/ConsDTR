using System.Diagnostics;
using ConsDTR.Lib.Models;
using ConsDTR.Runtime;
using ConsDTR.Runtime.Tasks;
using TorchSharp;
using static TorchSharp.torch;

namespace ConsDTR.Lib.Inference;

/// <summary>Executes explicit fallback/teacher plans through the same metered hard routing and final gate.</summary>
public sealed class DtrTaskPlanExecutor(DtrNeuralModel? model = null)
{
    public DtrInferenceResult RunHornFallback(HornProblem problem, SessionOptions? options = null) =>
        Execute(new HornTask(problem), charge => HornFairScheduler.BuildPlan(problem, charge),
            (options ?? new()) with { Mode = ExecutionMode.FairFallback });

    public DtrInferenceResult Execute(IKernelTask task, Func<Action<long>, TaskExecutionPlan> buildPlan,
        SessionOptions? options = null)
    {
        options ??= new SessionOptions { Mode = ExecutionMode.OracleDiagnostic };
        if (model is not null) options = options with { ModelCheckpointDigest = DtrCheckpointStore.ComputeModelDigest(model) };
        if (options.Mode == ExecutionMode.LearnedOnly)
            throw new ArgumentException("Reference plans cannot be used in LearnedOnly mode.");
        model?.FreezeForInference();
        using var noGradient = no_grad();
        using var runtime = RuntimeSession.Create(task, options);
        var clock = Stopwatch.StartNew();
        Tensor? hidden = null;
        Tensor? goal = null;
        IReadOnlyList<SlotState>? previousSlots = null;
        long edits = 0;
        long rejected = 0;
        try
        {
            var plan = buildPlan(cost => runtime.RecordAudit("REFERENCE_PLANNING_WORK", new { Work = cost }, cost));
            if (plan.Mode != options.Mode) throw new ArgumentException("Plan identity differs from the declared run mode.");
            runtime.RecordAudit("REFERENCE_PLAN", plan, plan.Actions.Length + 1L);
            var bindings = runtime.InputSlots.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
            if (model is not null)
            {
                using var scope = NewDisposeScope();
                goal = model.EncodeGoal(DtrFeatureEncoder.EncodeGoal(task.Definition, model.Config, model.Device)).DetachFromDisposeScope();
            }
            foreach (var action in plan.Actions)
            {
                var initial = runtime.Snapshot();
                var worker = initial.Slots.First(s => s.Kind == SlotKind.Worker && s.Active).Handle;
                var sources = action.OperandBindings.Select(binding => bindings.TryGetValue(binding, out var handle) ? handle :
                    throw new InvalidDataException("Planned operand has no accepted exact record: " + binding)).ToArray();
                if (sources.Length > options.MaxInDegree)
                    return Finish(DtrResultStatus.Unsupported, "RULE_OR_CHECK_ARITY_EXCEEDS_INBOX_CAPACITY");
                var noOp = action.Operation == "NO_OP";
                var target = noOp ? default : initial.Slots.FirstOrDefault(s => s.Active && s.Kind == SlotKind.Register && s.RecordId is null)?.Handle
                    ?? throw new RuntimeResourceException("No free result register.");
                runtime.RecordAudit("FAIR_PROTECTION_BEGIN", new { worker, target, Sources = sources, action.Operation }, sources.Length + 1);
                var desired = sources.Select((source, index) => new TopologyEdit(TopologyEditKind.AddEdge, source, worker,
                    action.OperandBindings[index].StartsWith("rule:", StringComparison.Ordinal) ? EdgeRole.RuleInput : EdgeRole.EvidenceInput,
                    $"p{index}")).ToList();
                if (!noOp) desired.Add(new(TopologyEditKind.AddEdge, worker, target, EdgeRole.WriteResult, "write"));
                var old = initial.Edges.Where(e => e.Source == worker || e.Target == worker).ToArray();
                var changes = old.Where(e => !desired.Any(d => Matches(e, d))).Select(e => new TopologyEdit(TopologyEditKind.RemoveEdge,
                    e.Source, e.Target, EdgeId: e.EdgeId)).Concat(desired.Where(d => !old.Any(e => Matches(e, d)))).ToArray();
                var offset = 0;
                var completed = false;
                do
                {
                    if (!runtime.BeginStep()) return Finish(DtrResultStatus.Unknown, "MICROSTEP_BUDGET_EXHAUSTED");
                    var batch = new List<TopologyEdit>();
                    var endpoints = new HashSet<SlotHandle>();
                    while (offset < changes.Length && batch.Count < options.MaxEdgeEditsPerStep)
                    {
                        var change = changes[offset];
                        if (endpoints.Append(change.Source).Append(change.Target).Distinct().Count() > options.MaxEditedEndpointsPerStep) break;
                        batch.Add(change); endpoints.Add(change.Source); endpoints.Add(change.Target); offset++;
                    }
                    if (batch.Count == 0 && offset < changes.Length) throw new RuntimeResourceException("A single routed edit does not fit the declared local support.");
                    var snapshot = runtime.Snapshot();
                    if (model is not null) DtrInferenceRunner.ChargeNeuralStep(runtime, snapshot, model, includePolicy: false);
                    var result = runtime.ApplyTopology(new(batch));
                    if (!result.Accepted) { runtime.EndStep(); rejected++; return Finish(DtrResultStatus.ResourceOrNumericFailure, result.Reason); }
                    edits += batch.Count;
                    var frame = runtime.Transport();
                    AdvanceNeural(runtime.Snapshot());
                    if (offset == changes.Length)
                    {
                        if (!noOp)
                        {
                            var read = sources.Select(source => frame.ForWorker(worker).Single(r => r.Source == source)).ToArray();
                            var write = frame.WriteFor(worker, target) ?? throw new InvalidDataException("Missing planned write receipt.");
                            var committed = runtime.Commit(new(worker, action.Operation, read, write, action.Candidate, action.Argument));
                            if (!committed.Accepted) { runtime.EndStep(); rejected++; return Finish(DtrResultStatus.ResourceOrNumericFailure, committed.Reason); }
                            bindings.Add(action.ResultBinding, target);
                        }
                        completed = true;
                    }
                    runtime.EndStep();
                } while (!completed);
                runtime.RecordAudit("FAIR_PROTECTION_END", new { worker, target, action.ResultBinding }, 1);
            }
            if (!bindings.TryGetValue(plan.OutputBinding, out var outputHandle))
                return Finish(DtrResultStatus.Unknown, "NO_ACCEPTED_OUTPUT_BINDING");
            var final = runtime.Snapshot();
            var recordId = final.Slots.Single(s => s.Handle == outputHandle).RecordId;
            var output = final.Records.Single(r => r.RecordId == recordId).Value;
            var certificate = runtime.Seal(output);
            var verification = IndependentVerifier.Verify(certificate, task.CreateIndependentVerifier(), runtime.TrustedEndAnchor!, options.FinalGateWorkAllowance);
            return Finish(verification.IsNegative ? DtrResultStatus.VerifiedNegative : DtrResultStatus.VerifiedResult,
                plan.CompletionReason, certificate, verification);
        }
        catch (Exception error) when (error is RuntimeResourceException or DtrNumericException or InvalidDataException)
        {
            try { runtime.EndStep(); } catch (InvalidOperationException) { }
            return Finish(DtrResultStatus.ResourceOrNumericFailure, error.Message);
        }
        finally { hidden?.Dispose(); goal?.Dispose(); }

        void AdvanceNeural(RuntimeSnapshot snapshot)
        {
            if (model is null) return;
            using var scope = NewDisposeScope();
            using var encoded = DtrFeatureEncoder.EncodeSnapshot(snapshot, model.Config, model.Device);
            var local = model.EncodeLocal(encoded.Features);
            var prior = DtrFeatureEncoder.SynchronizeHidden(model, previousSlots, hidden, encoded, local);
            var next = model.ForwardAlongEdges(prior, local, goal!, snapshot.Edges.Select(e => encoded.SlotIndices[e.Source.SlotId]).ToArray(),
                snapshot.Edges.Select(e => encoded.SlotIndices[e.Target.SlotId]).ToArray(), snapshot.Edges.Select(e => (int)e.Role).ToArray(),
                encoded.Slots.Select(s => s.Active).ToArray());
            DtrNeuralModel.EnsureFinite(next, "fair-step neural state");
            hidden?.Dispose(); hidden = next.DetachFromDisposeScope(); previousSlots = encoded.Slots;
        }
        DtrInferenceResult Finish(DtrResultStatus status, string reason, RuntimeCertificate? certificate = null, VerificationReport? verification = null) =>
            new(status, reason, options.Mode, runtime.Snapshot(), certificate, verification, null, rejected, edits,
                clock.Elapsed.TotalMilliseconds, runtime.TrustedEndAnchor);
    }

    private static bool Matches(DataEdge edge, TopologyEdit edit) => edge.Source == edit.Source && edge.Target == edit.Target &&
        edge.Role == edit.Role && edge.Port == edit.Port;
}
