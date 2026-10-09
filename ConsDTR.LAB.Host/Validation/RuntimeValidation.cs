using ConsDTR.Runtime;
using ConsDTR.Runtime.Tasks;

namespace ConsDTR.LAB.Host.Validation;

/// <summary>Finite adversarial regression checks, not a machine proof or learned-capability benchmark.</summary>
public static class RuntimeValidation
{
    public static IReadOnlyList<string> Run()
    {
        var passed = new List<string>();
        void Test(string name, Action check) { check(); passed.Add(name); }
        var task = ChainTask();
        var certificate = MakeChainCertificate(task, out var trustedAnchor);
        Test("Complete A -> B -> C semantic/route/authority certificate", () =>
            Assert(IndependentVerifier.Verify(certificate, task.CreateIndependentVerifier(), trustedAnchor, 500_000).Accepted));
        Test("Strict serialization roundtrip", () =>
            Assert(IndependentVerifier.Verify(RuntimeCertificate.Deserialize(certificate.Serialize()), task.CreateIndependentVerifier(), trustedAnchor, 500_000).Accepted));
        Test("Every individual journal event deletion is rejected", () =>
        {
            for (var i = 0; i < certificate.RuntimeJournal.Count; i++)
            {
                var altered = certificate with { RuntimeJournal = certificate.RuntimeJournal.Where((_, index) => index != i).ToArray() };
                Assert(!IndependentVerifier.Verify(altered, task.CreateIndependentVerifier(), trustedAnchor, 500_000).Accepted, "Deleted event " + i);
            }
        });
        Test("Every individual journal event duplication is rejected", () =>
        {
            for (var i = 0; i < certificate.RuntimeJournal.Count; i++)
            {
                var duplicated = certificate.RuntimeJournal.ToList(); duplicated.Insert(i, duplicated[i]);
                Assert(!IndependentVerifier.Verify(certificate with { RuntimeJournal = duplicated }, task.CreateIndependentVerifier(), trustedAnchor, 500_000).Accepted);
            }
        });
        Test("Adjacent event reorder and all truncated prefixes rejected", () =>
        {
            for (var i = 1; i < certificate.RuntimeJournal.Count; i++)
            {
                var changed = certificate.RuntimeJournal.ToArray(); (changed[i - 1], changed[i]) = (changed[i], changed[i - 1]);
                Assert(!IndependentVerifier.Verify(certificate with { RuntimeJournal = changed }, task.CreateIndependentVerifier(), trustedAnchor, 500_000).Accepted);
                Assert(!IndependentVerifier.Verify(certificate with { RuntimeJournal = certificate.RuntimeJournal.Take(i).ToArray() }, task.CreateIndependentVerifier(), trustedAnchor, 500_000).Accepted);
            }
        });
        Test("Semantic validity is separate from complete route validity", () =>
        {
            var unrelated = certificate.RuntimeJournal.Single(x => x.EventKind == "AUDIT");
            var report = IndependentVerifier.Verify(certificate with { RuntimeJournal = certificate.RuntimeJournal.Where(x => x != unrelated).ToArray() },
                task.CreateIndependentVerifier(), trustedAnchor, 500_000);
            Assert(report.SemanticVerified && !report.RouteReplayVerified && !report.Accepted);
        });
        Test("Certificate self-supplied anchor is insufficient", () =>
        {
            var report = IndependentVerifier.Verify(certificate, task.CreateIndependentVerifier(), null, 500_000);
            Assert(report.SemanticVerified && report.RouteReplayVerified && !report.ExecutionBound);
        });
        Test("Authority/checkpoint/mode/output substitution rejected", () =>
        {
            var verifier = task.CreateIndependentVerifier();
            Assert(!IndependentVerifier.Verify(certificate with { EndAnchor = certificate.EndAnchor with { AuthorityId = "forged" } }, verifier, trustedAnchor, 500_000).Accepted);
            Assert(!IndependentVerifier.Verify(certificate with { Manifest = certificate.Manifest with { ModelCheckpointDigest = "replacement" } }, verifier, trustedAnchor, 500_000).Accepted);
            Assert(!IndependentVerifier.Verify(certificate with { Manifest = certificate.Manifest with { ExecutionMode = ExecutionMode.LearnedOnly } }, verifier, trustedAnchor, 500_000).Accepted);
            Assert(!IndependentVerifier.Verify(certificate with { Manifest = certificate.Manifest with { ExactOutput = HornTask.Atom("B") } }, verifier, trustedAnchor, 500_000).Accepted);
            MakeChainCertificate(task, out var anotherAnchor);
            Assert(!IndependentVerifier.Verify(certificate, verifier, anotherAnchor, 500_000).Accepted);
        });
        Test("Recomputed digest chains cannot hide changed versions or deleted costs", () =>
        {
            var changed = certificate.RuntimeJournal.ToArray();
            var topology = Array.FindIndex(changed, x => x.EventKind == "TOPOLOGY_TRANSACTION");
            changed[topology] = changed[topology] with { PostTopologyRevision = changed[topology].PostTopologyRevision + 1 };
            var report = IndependentVerifier.Verify(certificate with { RuntimeJournal = Rechain(changed) }, task.CreateIndependentVerifier(), trustedAnchor, 500_000);
            Assert(report.SemanticVerified && !report.RouteReplayVerified);
            var omitted = certificate.RuntimeJournal.Where(x => x.EventKind != "AUDIT").ToArray();
            report = IndependentVerifier.Verify(certificate with { RuntimeJournal = Rechain(omitted) }, task.CreateIndependentVerifier(), trustedAnchor, 500_000);
            Assert(report.SemanticVerified && !report.RouteReplayVerified);
        });
        Test("Canonical duplicate/unknown/overflow fields rejected", () =>
        {
            Throws<InvalidDataException>(() => DtrCanonical.EncodeJson("{\"a\":1,\"a\":2}"));
            Throws<InvalidDataException>(() => DtrCanonical.EncodeJson("{\"a\":9223372036854775808}"));
            Throws<InvalidDataException>(() => DtrCanonical.Encode(new { Text = new string((char)0xD800, 1) }));
            Throws<System.Text.Json.JsonException>(() => RuntimeCertificate.Deserialize("{}"));
            Throws<System.Text.Json.JsonException>(() => RuntimeCertificate.Deserialize(certificate.Serialize()[..^1] + ",\"UnknownSemanticField\":1}"));
        });
        Test("Rejected topology transaction leaves complete structure unchanged", () =>
        {
            using var session = NewSession(task); session.BeginStep();
            var worker = Worker(session); var target = EmptyRegister(session); var input = session.InputSlots["fact:A"];
            var before = session.Snapshot();
            var transaction = new TopologyTransaction([
                new(TopologyEditKind.AddEdge, input, worker, EdgeRole.EvidenceInput, "premise0"),
                new(TopologyEditKind.AddEdge, worker, target, EdgeRole.WriteResult, "write"),
                new(TopologyEditKind.AddEdge, input, worker, EdgeRole.EvidenceInput, "premise0")]);
            Assert(!session.ApplyTopology(transaction).Accepted);
            var after = session.Snapshot();
            Assert(after.TopologyRevision == before.TopologyRevision && after.Edges.Count == 0 && after.ExactStateRevision == before.ExactStateRevision);
            Assert(after.RemainingWork < before.RemainingWork);
            session.EndStep();
        });
        Test("Random edit batches preserve invariants and rejected atomic rollback", () =>
        {
            using var session = NewSession(task); var random = new Random(431);
            for (var step = 0; step < 64; step++)
            {
                session.BeginStep(); var before = session.Snapshot(); var worker = Worker(session);
                var inputs = session.InputSlots.Values.ToArray();
                var proposals = Enumerable.Range(0, 1 + random.Next(3)).Select(_ => new TopologyEdit(TopologyEditKind.AddEdge,
                    inputs[random.Next(inputs.Length)], worker, EdgeRole.EvidenceInput, "p" + random.Next(5))).ToArray();
                var result = session.ApplyTopology(new(proposals)); var after = session.Snapshot();
                Assert(after.Edges.GroupBy(x => x.Target).All(x => x.Count() <= session.Options.MaxInDegree));
                if (!result.Accepted) Assert(DtrCanonical.Digest(before.Edges) == DtrCanonical.Digest(after.Edges) && before.TopologyRevision == after.TopologyRevision);
                session.EndStep();
            }
        });
        Test("NO_OP/retype revision and monotonic edge identity", () =>
        {
            using var session = NewSession(task); session.BeginStep(); var worker = Worker(session); var input = session.InputSlots["fact:A"];
            Assert(session.ApplyTopology(new([new(TopologyEditKind.NoOp)])).TopologyRevision == 0);
            var added = session.ApplyTopology(new([new(TopologyEditKind.AddEdge, input, worker, EdgeRole.EvidenceInput, "p")]));
            var edge = added.AddedEdges.Single(); var revision = added.TopologyRevision;
            Assert(!session.ApplyTopology(new([new(TopologyEditKind.RetypeEdge, Role: edge.Role, Port: edge.Port, EdgeId: edge.EdgeId)])).Changed);
            Assert(session.Snapshot().TopologyRevision == revision);
            Assert(session.ApplyTopology(new([new(TopologyEditKind.RetypeEdge, Role: EdgeRole.CandidateValue, Port: "candidate", EdgeId: edge.EdgeId)])).Accepted);
            Assert(session.Edges.Single().EdgeRevision == 1);
            Assert(session.ApplyTopology(new([new(TopologyEditKind.RemoveEdge, EdgeId: edge.EdgeId)])).Accepted);
            var next = session.ApplyTopology(new([new(TopologyEditKind.AddEdge, input, worker, EdgeRole.EvidenceInput, "p")]));
            Assert(next.AddedEdges.Single().EdgeId > edge.EdgeId); session.EndStep();
        });
        Test("Reset requires explicit edge deletion and invalidates generation", () =>
        {
            using var session = NewSession(task); session.BeginStep(); var worker = Worker(session);
            var edge = session.ApplyTopology(new([new(TopologyEditKind.AddEdge, session.InputSlots["fact:A"], worker, EdgeRole.EvidenceInput, "p")])).AddedEdges.Single();
            Assert(!session.ApplyTopology(new([new(TopologyEditKind.ResetWorker, Source: worker)])).Accepted);
            session.ApplyTopology(new([new(TopologyEditKind.RemoveEdge, EdgeId: edge.EdgeId)]));
            Assert(session.ApplyTopology(new([new(TopologyEditKind.ResetWorker, Source: worker)])).Accepted);
            Assert(!session.ApplyTopology(new([new(TopologyEditKind.ActivateSlot, Source: worker)])).Accepted);
            var replacement = worker with { Generation = worker.Generation + 1 };
            Assert(session.ApplyTopology(new([new(TopologyEditKind.ActivateSlot, Source: replacement)])).Accepted);
            Throws<InvalidDataException>(() => session.Probe([worker])); session.EndStep();
        });
        Test("Old snapshot read receipt and old proof are rejected after deleting edge", () =>
        {
            using var session = NewSession(task); session.BeginStep(); var worker = Worker(session); var destination = EmptyRegister(session);
            AddRuleRoute(session, session.InputSlots["fact:A"], session.InputSlots["rule:r1"], worker, destination);
            var old = session.Transport(); session.EndStep(); session.BeginStep();
            session.ApplyTopology(new(session.Edges.Select(x => new TopologyEdit(TopologyEditKind.RemoveEdge, EdgeId: x.EdgeId)).ToArray()));
            session.ApplyTopology(new([new(TopologyEditKind.AddEdge, worker, destination, EdgeRole.WriteResult, "write")]));
            var current = session.Transport();
            Assert(!session.Commit(new(worker, HornTask.ApplyRule, old.ForWorker(worker), current.WriteFor(worker, destination)!)).Accepted);
            Assert(session.Records.Count == task.Definition.Inputs.Count); session.EndStep();
        });
        Test("Cross-session receipts and unauthorized write targets rejected", () =>
        {
            using var first = NewSession(task); using var second = NewSession(task);
            first.BeginStep(); second.BeginStep(); var worker = Worker(first); var target = EmptyRegister(first);
            AddRuleRoute(first, first.InputSlots["fact:A"], first.InputSlots["rule:r1"], worker, target);
            AddRuleRoute(second, second.InputSlots["fact:A"], second.InputSlots["rule:r1"], Worker(second), EmptyRegister(second));
            var a = first.Transport(); var b = second.Transport();
            Assert(!first.Commit(new(worker, HornTask.ApplyRule, b.ForWorker(Worker(second)), a.WriteFor(worker, target)!)).Accepted);
            first.EndStep(); second.EndStep(); first.BeginStep(); var current = first.Transport();
            var forged = current.WriteFor(worker, target)! with { Target = first.InputSlots["fact:A"] };
            Assert(!first.Commit(new(worker, HornTask.ApplyRule, current.ForWorker(worker), forged)).Accepted); first.EndStep();
        });
        Test("Occupied immutable register cannot be overwritten", () =>
        {
            using var session = NewSession(task); var worker = Worker(session); var target = EmptyRegister(session);
            session.BeginStep(); AddRuleRoute(session, session.InputSlots["fact:A"], session.InputSlots["rule:r1"], worker, target);
            var frame = session.Transport(); Assert(session.Commit(new(worker, HornTask.ApplyRule, frame.ForWorker(worker), frame.WriteFor(worker, target)!)).Accepted);
            session.EndStep(); session.BeginStep(); frame = session.Transport();
            Assert(!session.Commit(new(worker, HornTask.ApplyRule, frame.ForWorker(worker), frame.WriteFor(worker, target)!)).Accepted); session.EndStep();
        });
        Test("Exact integer width and boolean type checks", () =>
        {
            using var session = NewSession(task); var worker = Worker(session); var target = EmptyRegister(session);
            session.BeginStep(); session.ApplyTopology(new([new(TopologyEditKind.AddEdge, worker, target, EdgeRole.WriteResult, "write")]));
            var frame = session.Transport();
            Assert(!session.Commit(new(worker, "PROPOSE_VALUE", [], frame.WriteFor(worker, target)!, new("int", "9223372036854775808"))).Accepted);
            session.EndStep(); session.BeginStep(); frame = session.Transport();
            Assert(!session.Commit(new(worker, "PROPOSE_VALUE", [], frame.WriteFor(worker, target)!, new("bool", "1"))).Accepted); session.EndStep();
        });
        Test("Mode cannot conceal fallback and sealed session cannot mutate", () =>
        {
            using var session = NewSession(task); session.SetMode(ExecutionMode.FairFallback);
            Throws<InvalidOperationException>(() => session.SetMode(ExecutionMode.LearnedOnly));
            CompleteChain(session); session.Seal(HornTask.Atom("C")); var count = session.Journal.Count;
            Throws<InvalidOperationException>(() => session.BeginStep());
            Throws<InvalidOperationException>(() => session.RecordAudit("POST_SEAL", new { X = 1 }, 1));
            Assert(session.Journal.Count == count);
        });
        Test("NO_OP still consumes one microstep and work", () =>
        {
            using var session = NewSession(task, Options() with { MaxMicrosteps = 1 });
            var before = session.Snapshot().RemainingWork;
            Assert(session.BeginStep()); session.ApplyTopology(new([new(TopologyEditKind.NoOp)])); session.EndStep();
            Assert(!session.BeginStep() && session.Snapshot().RemainingWork < before);
        });
        Test("Final verification allowance exhaustion never returns VERIFIED", () =>
        {
            var report = IndependentVerifier.Verify(certificate, task.CreateIndependentVerifier(), trustedAnchor, 1);
            Assert(!report.Accepted);
            using var session = NewSession(task, Options() with { FinalGateWorkAllowance = 1 }); CompleteChain(session);
            Throws<InvalidDataException>(() => session.Seal(HornTask.Atom("C")));
            Assert(!session.IsSealed && session.BeginStep()); session.EndStep();
        });
        Test("Journal capacity failure precedes graph mutation", () =>
        {
            using var session = NewSession(task, Options() with { MaxJournalEvents = 2 }); session.BeginStep();
            var before = session.Snapshot();
            Throws<RuntimeResourceException>(() => session.ApplyTopology(new([new(TopologyEditKind.AddEdge,
                session.InputSlots["fact:A"], Worker(session), EdgeRole.EvidenceInput, "p")])));
            Assert(session.Snapshot().TopologyRevision == before.TopologyRevision && session.Edges.Count == 0 && session.Journal.Count == 2);
        });
        Test("Explicit failure closes phases and STEP_END before resumed formal result", () =>
        {
            using var session = NewSession(task); session.BeginStep(); session.Transport();
            Assert(session.ReportFailure("NUMERIC_FAILURE", "Non-finite model state"));
            Assert(session.Journal[^2].EventKind == "FAILURE_CLOSE" && session.Journal[^1].EventKind == "STEP_END");
            CompleteChain(session);
            var sealedResult = session.Seal(HornTask.Atom("C"));
            Assert(IndependentVerifier.Verify(sealedResult, task.CreateIndependentVerifier(), session.TrustedEndAnchor, 500_000).Accepted);
        });
        Test("Insufficient failure closure capacity leaves the complete prefix unchanged", () =>
        {
            using var session = NewSession(task, Options() with { MaxJournalEvents = 3 }); session.BeginStep();
            var before = session.Journal;
            Assert(!session.ReportFailure("RESOURCE_FAILURE", "Capacity exhausted"));
            Assert(DtrCanonical.Digest(before) == DtrCanonical.Digest(session.Journal));
        });
        Test("Directory metadata is checked against exact runtime identities", () =>
        {
            using var session = NewSession(task); var slot = session.Slots[0];
            session.RecordAudit("DIRECTORY_UPSERT", new ConsDTR.Runtime.Discovery.CandidateHeader(
                checked((int)slot.Handle.SlotId), 999, slot.BindingRevision, slot.MetadataRevision, "forged", slot.Kind.ToString()), 1);
            CompleteChain(session);
            Throws<InvalidDataException>(() => session.Seal(HornTask.Atom("C")));
            Assert(!session.IsSealed);
        });
        Test("Committed WAL prefix recovery and interrupted transport abort", () => WalRecovery(task));
        Test("Untrusted recovery head substitution rejected", () =>
            Throws<InvalidDataException>(() => RuntimeSession.Recover(task, certificate.RuntimeJournal, "forged-head")));
        Test("Persistent external authority anchor and bounded sealed-only exporter", () =>
        {
            var prefix = Path.Combine(Path.GetTempPath(), "consdtr-export-" + Guid.NewGuid().ToString("N"));
            var certificatePath = prefix + ".certificate.json"; var anchorPath = prefix + ".expected-anchor.json";
            var rejectedPath = prefix + ".rejected.json";
            try
            {
                using var session = NewSession(task);
                Throws<InvalidOperationException>(() => ExpectedEndAnchorStore.Save(session, anchorPath));
                CompleteChain(session); var result = session.Seal(HornTask.Atom("C"));
                ExpectedEndAnchorStore.Save(session, anchorPath);
                var persistedExpected = ExpectedEndAnchorStore.Load(anchorPath);
                Assert(persistedExpected == session.TrustedEndAnchor);
                var receipt = SealedExporter.Export(result, certificatePath, task.CreateIndependentVerifier(), persistedExpected, 500_000);
                Assert(receipt.Verification.Accepted && receipt.SerializedBytes == new FileInfo(certificatePath).Length);
                Assert(IndependentVerifier.Verify(SealedExporter.Load(certificatePath), task.CreateIndependentVerifier(), persistedExpected, 500_000).Accepted);
                Throws<InvalidDataException>(() => SealedExporter.Export(result with { RuntimeJournal = result.RuntimeJournal.Skip(1).ToArray() },
                    rejectedPath, task.CreateIndependentVerifier(), persistedExpected, 500_000));
                Assert(!File.Exists(rejectedPath));
                Throws<RuntimeResourceException>(() => SealedExporter.Export(result, rejectedPath, task.CreateIndependentVerifier(), persistedExpected, 500_000, 1));
                Assert(!File.Exists(rejectedPath));
                Throws<RuntimeResourceException>(() => SealedExporter.Load(certificatePath, 1));
            }
            finally
            {
                foreach (var path in new[] { certificatePath, anchorPath, rejectedPath }) if (File.Exists(path)) File.Delete(path);
            }
        });
        return passed.AsReadOnly();
    }

    private static void WalRecovery(HornTask task)
    {
        var path = Path.Combine(Path.GetTempPath(), "consdtr-runtime-" + Guid.NewGuid().ToString("N") + ".wal");
        try
        {
            IReadOnlyList<JournalEvent> expected;
            using (var original = NewSession(task, Options() with { JournalPath = path }))
            {
                original.BeginStep(); original.RecordAudit("RECOVERY_TEST", new { Version = 1 }, 1); original.EndStep();
                expected = original.Journal;
            }
            using (var append = new FileStream(path, FileMode.Append, FileAccess.Write)) append.Write([0, 0]);
            var recoveredPrefix = JournalStore.ReadCommitted(path);
            Assert(DtrCanonical.Digest(expected) == DtrCanonical.Digest(recoveredPrefix));
            using (var recovered = RuntimeSession.Recover(task, recoveredPrefix, expected[^1].EventDigest))
            {
                Assert(recovered.Snapshot().RemainingMicrosteps == 127 && recovered.Snapshot().MicrostepId == 0);
                recovered.BeginStep(); recovered.Transport(); recovered.EndStep();
                Assert(JournalStore.ReadCommitted(path).Count == recovered.Journal.Count);
            }
            var events = JournalStore.ReadCommitted(path);
            var transportBegin = events.Last(x => x.EventKind == "TRANSPORT_BEGIN");
            var interrupted = events.Take((int)transportBegin.EventSeq + 1).ToArray();
            // A separately stored committed prefix is restored with an explicit abort record.
            JournalStore.WriteComplete(path, interrupted);
            using var aborted = RuntimeSession.Recover(task, interrupted, interrupted[^1].EventDigest);
            Assert(aborted.Journal.Any(x => x.EventKind == "RECOVERY_ABORT_PHASE") && aborted.Journal[^1].EventKind == "STEP_END" && aborted.BeginStep()); aborted.EndStep();
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static HornTask ChainTask() => new(new HornProblem(["A", "B", "C"], ["A"],
        [new("r1", ["A"], "B"), new("r2", ["B"], "C")], "C"));
    private static SessionOptions Options() => new()
    {
        WorkerCount = 1, RegisterCount = 12, MaxSlots = 32, MaxMicrosteps = 128,
        MaxWork = 10_000_000, FinalGateWorkAllowance = 500_000, ModelCheckpointDigest = "runtime-validation-untrained"
    };
    private static RuntimeSession NewSession(HornTask task, SessionOptions? options = null) => RuntimeSession.Create(task, options ?? Options());
    private static SlotHandle Worker(RuntimeSession session) => session.Slots.First(x => x.Kind == SlotKind.Worker && x.Active).Handle;
    private static SlotHandle EmptyRegister(RuntimeSession session) => session.Slots.First(x => x.Kind == SlotKind.Register && x.Active && x.RecordId is null).Handle;
    private static void AddRuleRoute(RuntimeSession session, SlotHandle premise, SlotHandle rule, SlotHandle worker, SlotHandle target)
    {
        var result = session.ApplyTopology(new([
            new(TopologyEditKind.AddEdge, premise, worker, EdgeRole.EvidenceInput, "premise0"),
            new(TopologyEditKind.AddEdge, rule, worker, EdgeRole.RuleInput, "rule"),
            new(TopologyEditKind.AddEdge, worker, target, EdgeRole.WriteResult, "write")]));
        Assert(result.Accepted, result.Reason);
    }
    private static void CompleteChain(RuntimeSession session)
    {
        var worker = Worker(session); session.BeginStep(); var b = EmptyRegister(session);
        AddRuleRoute(session, session.InputSlots["fact:A"], session.InputSlots["rule:r1"], worker, b);
        var frame = session.Transport(); Assert(session.Commit(new(worker, HornTask.ApplyRule, frame.ForWorker(worker), frame.WriteFor(worker, b)!)).Accepted);
        session.EndStep(); session.BeginStep(); var c = EmptyRegister(session);
        Assert(session.ApplyTopology(new(session.Edges.Select(edge => new TopologyEdit(TopologyEditKind.RemoveEdge, EdgeId: edge.EdgeId)).ToArray())).Accepted);
        AddRuleRoute(session, b, session.InputSlots["rule:r2"], worker, c);
        frame = session.Transport(); Assert(session.Commit(new(worker, HornTask.ApplyRule, frame.ForWorker(worker), frame.WriteFor(worker, c)!)).Accepted);
        session.EndStep();
    }
    private static RuntimeCertificate MakeChainCertificate(HornTask task, out EndAnchor trusted)
    {
        using var session = NewSession(task); session.SetMode(ExecutionMode.FairFallback);
        session.RecordAudit("UNRELATED_AUDIT", new { ImportantForFullPrefix = true }, 1);
        CompleteChain(session); var certificate = session.Seal(HornTask.Atom("C")); trusted = session.TrustedEndAnchor!; return certificate;
    }
    private static void Assert(bool value, string? reason = null)
    { if (!value) throw new InvalidOperationException("Runtime validation failed: " + (reason ?? "assertion")); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected rejection: " + typeof(T).Name);
    }
    private static IReadOnlyList<JournalEvent> Rechain(IReadOnlyList<JournalEvent> events)
    {
        var changed = new JournalEvent[events.Count]; var previous = string.Empty;
        for (var i = 0; i < events.Count; i++)
        {
            var entry = events[i] with { EventSeq = i, PrevEventDigest = previous, EventDigest = string.Empty };
            changed[i] = entry with { EventDigest = entry.ComputeDigest() }; previous = changed[i].EventDigest;
        }
        return changed;
    }
}
