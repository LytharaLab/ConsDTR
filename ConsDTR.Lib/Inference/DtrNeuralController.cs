using ConsDTR.Lib.Models;
using ConsDTR.Runtime;
using ConsDTR.Runtime.Discovery;
using TorchSharp;
using static TorchSharp.torch;

namespace ConsDTR.Lib.Inference;

/// <summary>Hard proposals on bounded, public structural support. It never checks semantic truth.</summary>
internal sealed class DtrNeuralController(DtrNeuralModel model, DtrDiscretePolicy policy, DtrRandomStream? editorRandom = null,
    Action<DtrTeacherChoiceContext>? teacherContext = null)
{
    public TopologyTransaction DecodeEdits(RuntimeSnapshot snapshot, IReadOnlyList<DirectoryPage> pages,
        DtrEncodedState encoded, Tensor hidden, Tensor local, Tensor goal, SlotHandle worker,
        SessionOptions options, DtrPolicyTrace trace, DtrEditorMode mode, IReadOnlyCollection<SlotHandle>? controlReads = null,
        Tensor? intentContext = null)
    {
        using var scope = NewDisposeScope();
        if (mode == DtrEditorMode.Frozen) return new TopologyTransaction([]);
        var editPolicy = mode == DtrEditorMode.RandomLegal ? new DtrDiscretePolicy(model, editorRandom, deterministic: false) : policy;
        var slots = snapshot.Slots.ToDictionary(s => checked((int)s.Handle.SlotId));
        bool Current(CandidateHeader header) => slots.TryGetValue(header.Slot, out var slot) &&
            slot.Handle.Generation == header.Generation && slot.BindingRevision == header.BindingRevision &&
            slot.MetadataRevision == header.MetadataRevision && slot.Active == header.Active &&
            (!slot.Active || controlReads is null || controlReads.Contains(slot.Handle));
        pages = pages.Where(page => Current(page.Target)).Select(page => page with
            { Candidates = page.Candidates.Where(Current).ToArray() }).ToArray();
        var exposed = pages.SelectMany(p => p.Candidates.Append(p.Target)).Select(h => h.Slot).Distinct().ToHashSet();
        var preview = snapshot.Edges.ToList();
        var templates = RouteTemplates(pages, slots, options).ToArray();
        var byPair = templates.GroupBy(edit => (edit.Source, edit.Target)).ToDictionary(group => group.Key, group => group.ToArray());
        var support = new HashSet<SlotHandle>();
        var edits = new List<TopologyEdit>();
        var context = intentContext ?? model.CreatePolicyContext(hidden, local, goal, encoded.SlotIndices[worker.SlotId], Budget(snapshot));
        var slotOps = 0;
        var edgeOps = 0;
        for (var decision = 0; decision < options.MaxEdgeEditsPerStep + options.MaxSlotOperationsPerStep; decision++)
        {
            var index = new EdgeIndex(preview);
            var adds = templates.Where(edit => index.CanAdd(edit, options, support)).ToArray();
            var removable = preview.Where(e => e.EdgeId >= 0 && exposed.Contains(checked((int)e.Source.SlotId)) && exposed.Contains(checked((int)e.Target.SlotId)) &&
                FitsSupport(support, e.Source, e.Target, options)).ToArray();
            var retypes = removable.SelectMany(old => byPair.GetValueOrDefault((old.Source, old.Target), [])
                .Where(add => (add.Role != old.Role || add.Port != old.Port) && index.CanAdd(add, options, support, old))
                .Select(add => add with { Kind = TopologyEditKind.RetypeEdge, EdgeId = old.EdgeId })).ToArray();
            var resets = snapshot.Slots.Where(s => s.Active && s.Kind == SlotKind.Worker && s.RecordId is null &&
                exposed.Contains(checked((int)s.Handle.SlotId)) && index.InDegree(s.Handle) == 0 && index.OutDegree(s.Handle) == 0 &&
                FitsSupport(support, s.Handle, s.Handle, options)).ToArray();
            var activations = snapshot.Slots.Where(s => !s.Active && exposed.Contains(checked((int)s.Handle.SlotId)) &&
                FitsSupport(support, s.Handle, s.Handle, options)).ToArray();
            var kinds = new List<int> { (int)DtrEditKind.EndEdits, (int)DtrEditKind.NoOp };
            if (edgeOps < options.MaxEdgeEditsPerStep)
            {
                if (adds.Length > 0) kinds.Add((int)DtrEditKind.AddEdge);
                if (removable.Length > 0) kinds.Add((int)DtrEditKind.RemoveEdge);
                if (retypes.Length > 0) kinds.Add((int)DtrEditKind.RetypeEdge);
            }
            if (slotOps < options.MaxSlotOperationsPerStep)
            {
                if (resets.Length > 0) kinds.Add((int)DtrEditKind.ResetWorker);
                if (activations.Length > 0) kinds.Add((int)DtrEditKind.ActivateSlot);
            }
            teacherContext?.Invoke(new($"edit.{decision}.kind", snapshot, EditChoices: adds));
            var kind = mode == DtrEditorMode.RandomLegal ? editPolicy.Choose(zeros([7], device: model.Device), DtrPolicyField.EditKind, $"edit.{decision}.kind", kinds) :
                editPolicy.ChooseField(context, DtrPolicyField.EditKind, $"edit.{decision}.kind", kinds);
            trace.Add(kind);
            context = model.AdvancePolicyContext(context, DtrPolicyField.EditKind, kind.Choice);
            if (kind.Choice == (int)DtrEditKind.EndEdits) break;
            if (kind.Choice == (int)DtrEditKind.NoOp) { edits.Add(new(TopologyEditKind.NoOp)); continue; }
            TopologyEdit edit;
            if (kind.Choice == (int)DtrEditKind.AddEdge)
            {
                var logits = ScoreOptions(adds, index);
                teacherContext?.Invoke(new($"edit.{decision}.route", snapshot, EditChoices: adds));
                var choice = editPolicy.Choose(logits, DtrPolicyField.Pointer, $"edit.{decision}.route");
                trace.Add(choice);
                context = model.AdvancePolicyContext(context, DtrPolicyField.Pointer, choice.Choice);
                edit = adds[choice.Choice];
                preview.Add(new DataEdge(-1 - decision, edit.Source, edit.Target, edit.Role, edit.Port));
                edgeOps++;
            }
            else if (kind.Choice == (int)DtrEditKind.RemoveEdge)
            {
                var removeChoices = removable.Select(edge => new TopologyEdit(TopologyEditKind.RemoveEdge,
                    edge.Source, edge.Target, edge.Role, edge.Port, edge.EdgeId)).ToArray();
                var logits = ScoreOptions(removeChoices, index);
                teacherContext?.Invoke(new($"edit.{decision}.remove", snapshot, EditChoices: removeChoices));
                var choice = editPolicy.Choose(logits, DtrPolicyField.Pointer, $"edit.{decision}.remove");
                trace.Add(choice);
                context = model.AdvancePolicyContext(context, DtrPolicyField.Pointer, choice.Choice);
                var selected = removable[choice.Choice];
                edit = new(TopologyEditKind.RemoveEdge, selected.Source, selected.Target, EdgeId: selected.EdgeId);
                preview.Remove(selected);
                edgeOps++;
            }
            else if (kind.Choice == (int)DtrEditKind.RetypeEdge)
            {
                var logits = ScoreOptions(retypes, index);
                teacherContext?.Invoke(new($"edit.{decision}.retype", snapshot, EditChoices: retypes));
                var choice = editPolicy.Choose(logits, DtrPolicyField.Pointer, $"edit.{decision}.retype");
                trace.Add(choice);
                context = model.AdvancePolicyContext(context, DtrPolicyField.Pointer, choice.Choice);
                edit = retypes[choice.Choice];
                var old = preview.Single(e => e.EdgeId == edit.EdgeId);
                preview[preview.IndexOf(old)] = old with { Role = edit.Role, Port = edit.Port, EdgeRevision = old.EdgeRevision + 1 };
                edgeOps++;
            }
            else
            {
                var candidates = kind.Choice == (int)DtrEditKind.ResetWorker ? resets : activations;
                var logits = mode == DtrEditorMode.RandomLegal || kind.Choice == (int)DtrEditKind.ActivateSlot ? zeros([candidates.Length], device: model.Device) :
                    model.PointerLogits(context, hidden, local, candidates.Select(s => encoded.SlotIndices[s.Handle.SlotId]).ToArray());
                teacherContext?.Invoke(new($"edit.{decision}.slot", snapshot, SlotChoices: candidates.Select(s => s.Handle).ToArray()));
                var choice = editPolicy.Choose(logits, DtrPolicyField.Pointer, $"edit.{decision}.slot");
                trace.Add(choice);
                context = model.AdvancePolicyContext(context, DtrPolicyField.Pointer, choice.Choice);
                edit = new(kind.Choice == (int)DtrEditKind.ResetWorker ? TopologyEditKind.ResetWorker : TopologyEditKind.ActivateSlot,
                    candidates[choice.Choice].Handle);
                // Lifecycle decisions conclude this batch, so no old generation is reused by a later edit.
                slotOps++;
            }
            edits.Add(edit);
            support.Add(edit.Source);
            if (edit.Kind is TopologyEditKind.AddEdge or TopologyEditKind.RemoveEdge or TopologyEditKind.RetypeEdge) support.Add(edit.Target);
            if (edit.Kind is TopologyEditKind.ResetWorker or TopologyEditKind.ActivateSlot)
            {
                // Lifecycle changes invalidate the old endpoint view; END is forced by this support.
                teacherContext?.Invoke(new("edit.lifecycle-end", snapshot));
                trace.Add(editPolicy.ChooseField(context, DtrPolicyField.EditKind, "edit.lifecycle-end", [(int)DtrEditKind.EndEdits]));
                break;
            }
        }
        return new TopologyTransaction(edits);

        Tensor ScoreOptions(TopologyEdit[] choices, EdgeIndex index)
        {
            if (mode == DtrEditorMode.RandomLegal) return zeros([choices.Length], device: model.Device);
            var features = new float[choices.Length * 8];
            for (var j = 0; j < choices.Length; j++)
            {
                var edit = choices[j];
                features[j * 8] = PortIndex(edit.Port) / (float)model.Config.PortClasses;
                features[j * 8 + 1] = (int)slots[checked((int)edit.Source.SlotId)].Kind / 3f;
                features[j * 8 + 2] = (int)slots[checked((int)edit.Target.SlotId)].Kind / 3f;
                features[j * 8 + 3] = index.OutDegree(edit.Source) / (float)options.MaxOutDegree;
                features[j * 8 + 4] = index.InDegree(edit.Target) / (float)options.MaxInDegree;
                features[j * 8 + 5] = (int)edit.Kind / 6f;
            }
            return model.ScoreRoutes(hidden, local, goal,
                choices.Select(edit => encoded.SlotIndices[edit.Source.SlotId]).ToArray(),
                choices.Select(edit => encoded.SlotIndices[edit.Target.SlotId]).ToArray(),
                choices.Select(edit => (int)edit.Role).ToArray(), tensor(features, device: model.Device).reshape(choices.Length, 8));
        }
    }

    public (KernelAction? Action, bool Stop) DecodeAction(IKernelTask task, RuntimeSnapshot snapshot,
        TransportFrame frame, DtrEncodedState encoded, Tensor hidden, Tensor local, Tensor goal,
        SlotHandle worker, DtrPolicyTrace trace, Func<string, Tensor, DtrPolicyTrace, IReadOnlyList<ReadReceipt>, ExactValue?>? generate)
    {
        using var scope = NewDisposeScope();
        var context = model.CreatePolicyContext(hidden, local, goal, encoded.SlotIndices[worker.SlotId], Budget(snapshot));
        var vocabulary = DtrActionVocabulary.ForTask(task);
        if (vocabulary.Count > model.Config.OperationClasses) throw new ArgumentException("Operation head is smaller than the task vocabulary.");
        teacherContext?.Invoke(new("action.operation", snapshot, Operations: vocabulary));
        var operation = policy.ChooseField(context, DtrPolicyField.Operation, "action.operation", Enumerable.Range(0, vocabulary.Count).ToArray());
        trace.Add(operation);
        context = model.AdvancePolicyContext(context, DtrPolicyField.Operation, operation.Choice);
        var read = frame.ForWorker(worker);
        teacherContext?.Invoke(new("action.operand-count", snapshot, ReadChoices: read));
        var length = policy.ChooseField(context, DtrPolicyField.SequenceLength, "action.operand-count", Enumerable.Range(0, Math.Min(read.Count, model.Config.MaximumSequenceLength) + 1).ToArray());
        trace.Add(length);
        context = model.AdvancePolicyContext(context, DtrPolicyField.SequenceLength, length.Choice);
        var remaining = read.ToList();
        var selected = new List<ReadReceipt>();
        for (var i = 0; i < length.Choice; i++)
        {
            // Pointer scores read only the worker's context and fixed inbox header positions.
            // No unrestricted source hidden-state reads are introduced at action time.
            var logits = model.FieldLogits(context, DtrPolicyField.Port).slice(0, 0, remaining.Count, 1);
            teacherContext?.Invoke(new($"action.operand{i}", snapshot, ReadChoices: remaining));
            var pointer = policy.Choose(logits, DtrPolicyField.Pointer, $"action.operand{i}");
            trace.Add(pointer);
            selected.Add(remaining[pointer.Choice]);
            remaining.RemoveAt(pointer.Choice);
            context = model.AdvancePolicyContext(context, DtrPolicyField.Pointer, pointer.Choice);
        }
        var writes = frame.WriteReceipts.Where(w => w.Worker == worker).ToArray();
        KernelAction? action = null;
        if (writes.Length > 0)
        {
            teacherContext?.Invoke(new("action.write-target", snapshot, WriteChoices: writes));
            var pointer = policy.Choose(model.FieldLogits(context, DtrPolicyField.Port).slice(0, 0, writes.Length, 1),
                DtrPolicyField.Pointer, "action.write-target");
            trace.Add(pointer);
            context = model.AdvancePolicyContext(context, DtrPolicyField.Pointer, pointer.Choice);
            var op = vocabulary[operation.Choice];
            action = new(worker, op, selected, writes[pointer.Choice], generate?.Invoke(op, context, trace, selected));
        }
        teacherContext?.Invoke(new("action.stop", snapshot));
        var stop = policy.ChooseField(context, DtrPolicyField.Stop, "action.stop");
        trace.Add(stop);
        return (action, stop.Choice == 1);
    }

    public static float[] Budget(RuntimeSnapshot snapshot) =>
        [(float)Math.Log(1 + snapshot.RemainingMicrosteps) / 32,
            (float)Math.Log(1 + snapshot.RemainingWork) / 32, snapshot.Edges.Count / 2048f, snapshot.Proofs.Count / 4096f];

    private IEnumerable<TopologyEdit> RouteTemplates(IReadOnlyList<DirectoryPage> pages, Dictionary<int, SlotState> slots,
        SessionOptions options)
    {
        foreach (var page in pages)
        foreach (var header in page.Candidates)
        {
            if (!slots.TryGetValue(header.Slot, out var source) || !slots.TryGetValue(page.Target.Slot, out var target) ||
                !source.Active || !target.Active || source.Handle.Generation != header.Generation ||
                target.Handle.Generation != page.Target.Generation) continue;
            if (target.Kind == SlotKind.Worker && source.Kind != SlotKind.Worker)
            {
                foreach (var role in new[] { EdgeRole.EvidenceInput, EdgeRole.RuleInput, EdgeRole.CandidateValue })
                for (var port = 0; port < Math.Min(options.MaxInDegree, model.Config.PortClasses); port++)
                {
                    var name = role == EdgeRole.RuleInput ? "rule" : $"p{port}";
                    if (role == EdgeRole.RuleInput && port != 0) continue;
                    var edit = new TopologyEdit(TopologyEditKind.AddEdge, source.Handle, target.Handle, role, name);
                    yield return edit;
                }
            }
            if (target.Kind == SlotKind.Worker && source.Kind == SlotKind.Register && source.RecordId is null)
            {
                var edit = new TopologyEdit(TopologyEditKind.AddEdge, target.Handle, source.Handle, EdgeRole.WriteResult, "write");
                yield return edit;
            }
            var message = new TopologyEdit(TopologyEditKind.AddEdge, source.Handle, target.Handle, EdgeRole.NeuralMessage, "message");
            yield return message;
        }
    }

    private sealed class EdgeIndex
    {
        private readonly Dictionary<SlotHandle, int> incoming = [];
        private readonly Dictionary<SlotHandle, int> outgoing = [];
        private readonly HashSet<(SlotHandle Source, SlotHandle Target, EdgeRole Role, string Port)> identities = [];
        private readonly Dictionary<(SlotHandle Target, string Port), int> ports = [];

        public EdgeIndex(IEnumerable<DataEdge> edges)
        {
            foreach (var edge in edges)
            {
                incoming[edge.Target] = InDegree(edge.Target) + 1;
                outgoing[edge.Source] = OutDegree(edge.Source) + 1;
                identities.Add((edge.Source, edge.Target, edge.Role, edge.Port));
                if (edge.Role != EdgeRole.NeuralMessage) ports[(edge.Target, edge.Port)] = ports.GetValueOrDefault((edge.Target, edge.Port)) + 1;
            }
        }

        public int InDegree(SlotHandle slot) => incoming.GetValueOrDefault(slot);
        public int OutDegree(SlotHandle slot) => outgoing.GetValueOrDefault(slot);
        public bool CanAdd(TopologyEdit edit, SessionOptions options, HashSet<SlotHandle> support, DataEdge? removed = null)
        {
            if (!FitsSupport(support, edit.Source, edit.Target, options) ||
                OutDegree(edit.Source) - (removed?.Source == edit.Source ? 1 : 0) >= options.MaxOutDegree ||
                InDegree(edit.Target) - (removed?.Target == edit.Target ? 1 : 0) >= options.MaxInDegree) return false;
            var identity = (edit.Source, edit.Target, edit.Role, edit.Port);
            if (identities.Contains(identity) && (removed is null || identity != (removed.Source, removed.Target, removed.Role, removed.Port))) return false;
            var conflicts = ports.GetValueOrDefault((edit.Target, edit.Port));
            if (removed is not null && removed.Target == edit.Target && removed.Port == edit.Port && removed.Role != EdgeRole.NeuralMessage) conflicts--;
            return edit.Role == EdgeRole.NeuralMessage || conflicts == 0;
        }
    }

    private static bool FitsSupport(HashSet<SlotHandle> endpoints, SlotHandle source, SlotHandle target, SessionOptions options) =>
        endpoints.Append(source).Append(target).Distinct().Count() <= options.MaxEditedEndpointsPerStep;

    private static int PortIndex(string port) => port == "rule" ? 0 : port == "write" ? 1 :
        port.StartsWith('p') && int.TryParse(port.AsSpan(1), out var value) ? value + 2 : 2;
}
