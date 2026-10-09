namespace ConsDTR.Runtime;

internal static class TopologyEngine
{
    public static (RuntimeState State, TopologyResult Result) Evaluate(RuntimeState original,
        TopologyTransaction transaction, SessionOptions options)
    {
        try
        {
            if (transaction.Edits is null || transaction.Edits.Count > options.MaxEdgeEditsPerStep + options.MaxSlotOperationsPerStep + 1)
                throw new InvalidDataException("Topology batch too large.");
            var slots = original.Slots.ToDictionary(x => x.Handle.SlotId);
            var edges = original.Edges.ToDictionary(x => x.EdgeId);
            var added = new List<DataEdge>();
            var endpoints = new HashSet<SlotHandle>(original.EditedEndpoints);
            var edits = original.EdgeEditsThisStep;
            var slotOperations = original.SlotOperationsThisStep;
            var nextEdgeId = original.NextEdgeId;
            var changed = false;
            var bindingChanged = false;
            SlotState Live(SlotHandle handle)
            {
                if (!slots.TryGetValue(handle.SlotId, out var slot) || slot.Handle != handle || !slot.Active)
                    throw new InvalidDataException("Stale or inactive slot handle.");
                return slot;
            }

            foreach (var edit in transaction.Edits)
            {
                if (!Enum.IsDefined(edit.Kind) || !Enum.IsDefined(edit.Role)) throw new InvalidDataException("Unknown topology opcode.");
                switch (edit.Kind)
                {
                    case TopologyEditKind.NoOp: break;
                    case TopologyEditKind.AddEdge:
                        Live(edit.Source); Live(edit.Target);
                        endpoints.Add(edit.Source); endpoints.Add(edit.Target); edits++;
                        var edge = new DataEdge(nextEdgeId++, edit.Source, edit.Target, edit.Role, edit.Port);
                        edges.Add(edge.EdgeId, edge); added.Add(edge); changed = true;
                        break;
                    case TopologyEditKind.RemoveEdge:
                    case TopologyEditKind.RetypeEdge:
                        if (!edges.TryGetValue(edit.EdgeId, out var existing)) throw new InvalidDataException("Unknown edge identity.");
                        Live(existing.Source); Live(existing.Target);
                        endpoints.Add(existing.Source); endpoints.Add(existing.Target); edits++;
                        if (edit.Kind == TopologyEditKind.RemoveEdge) { edges.Remove(edit.EdgeId); changed = true; }
                        else if (existing.Role != edit.Role || existing.Port != edit.Port)
                        {
                            edges[edit.EdgeId] = existing with { Role = edit.Role, Port = edit.Port, EdgeRevision = checked(existing.EdgeRevision + 1) };
                            changed = true;
                        }
                        break;
                    case TopologyEditKind.ActivateSlot:
                        if (!slots.TryGetValue(edit.Source.SlotId, out var inactive) || inactive.Handle != edit.Source)
                            throw new InvalidDataException("Stale activation identity.");
                        endpoints.Add(edit.Source); slotOperations++;
                        if (!inactive.Active) { slots[edit.Source.SlotId] = inactive with { Active = true }; changed = true; }
                        break;
                    case TopologyEditKind.ResetWorker:
                        var worker = Live(edit.Source);
                        endpoints.Add(edit.Source); slotOperations++;
                        if (worker.Kind != SlotKind.Worker || worker.RecordId is not null) throw new InvalidDataException("Only an empty worker may reset.");
                        if (edges.Values.Any(x => x.Source == edit.Source || x.Target == edit.Source)) throw new InvalidDataException("Reset requires degree zero; delete edges explicitly.");
                        slots[edit.Source.SlotId] = worker with
                        {
                            Handle = worker.Handle with { Generation = checked(worker.Handle.Generation + 1) },
                            Active = false, BindingRevision = checked(worker.BindingRevision + 1), MetadataRevision = checked(worker.MetadataRevision + 1)
                        };
                        changed = true; bindingChanged = true;
                        break;
                }
            }
            if (edits > options.MaxEdgeEditsPerStep || slotOperations > options.MaxSlotOperationsPerStep || endpoints.Count > options.MaxEditedEndpointsPerStep)
                throw new InvalidDataException("Per-microstep edit/support budget exceeded.");
            var next = original with
            {
                Slots = slots.Values.OrderBy(x => x.Handle.SlotId).ToArray(),
                Edges = edges.Values.OrderBy(x => x.EdgeId).ToArray(),
                TopologyRevision = changed ? checked(original.TopologyRevision + 1) : original.TopologyRevision,
                ExactStateRevision = bindingChanged ? checked(original.ExactStateRevision + 1) : original.ExactStateRevision,
                NextEdgeId = nextEdgeId, EdgeEditsThisStep = edits, SlotOperationsThisStep = slotOperations,
                EditedEndpoints = endpoints.OrderBy(x => x.SlotId).ThenBy(x => x.Generation).ToArray()
            };
            CheckInvariants(next, options);
            return (next, new(true, changed, "Accepted", next.TopologyRevision, added.ToArray()));
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException)
        {
            return (original, new(false, false, error.Message, original.TopologyRevision, []));
        }
    }

    public static void CheckInvariants(RuntimeState state, SessionOptions options)
    {
        if (state.Slots.Length > options.MaxSlots || state.Slots.Select(x => x.Handle.SlotId).Distinct().Count() != state.Slots.Length)
            throw new InvalidDataException("Slot capacity/identity invariant failed.");
        var slots = state.Slots.ToDictionary(x => x.Handle);
        if (state.Edges.Select(x => x.EdgeId).Distinct().Count() != state.Edges.Length) throw new InvalidDataException("Repeated edge identity.");
        foreach (var edge in state.Edges)
        {
            if (edge.EdgeId < 0 || edge.EdgeRevision < 0 || !Enum.IsDefined(edge.Role) || string.IsNullOrEmpty(edge.Port) || edge.Port.Length > 64)
                throw new InvalidDataException("Invalid edge identity/type/port.");
            if (!slots.TryGetValue(edge.Source, out var source) || !slots.TryGetValue(edge.Target, out var target) || !source.Active || !target.Active)
                throw new InvalidDataException("Edge endpoint is not live.");
            if (edge.Role == EdgeRole.WriteResult)
            {
                if (source.Kind != SlotKind.Worker || target.Kind != SlotKind.Register) throw new InvalidDataException("WriteResult must join worker to register.");
            }
            else if (edge.Role != EdgeRole.NeuralMessage && (source.Kind == SlotKind.Worker || target.Kind != SlotKind.Worker))
                throw new InvalidDataException("Exact-read edge must join a storage slot to a worker.");
        }
        if (state.Edges.GroupBy(x => (x.Source, x.Target, x.Role, x.Port)).Any(x => x.Count() > 1)) throw new InvalidDataException("Duplicate data edge.");
        if (state.Edges.Where(x => x.Role != EdgeRole.NeuralMessage).GroupBy(x => (x.Target, x.Port)).Any(x => x.Count() > 1))
            throw new InvalidDataException("Conflicting exact target port.");
        if (state.Edges.GroupBy(x => x.Target).Any(x => x.Count() > options.MaxInDegree) || state.Edges.GroupBy(x => x.Source).Any(x => x.Count() > options.MaxOutDegree))
            throw new InvalidDataException("Degree limit exceeded.");
        if (state.Edges.Length > state.Slots.Count(x => x.Active) * (long)options.MaxInDegree) throw new InvalidDataException("Edge capacity exceeded.");
    }
}
