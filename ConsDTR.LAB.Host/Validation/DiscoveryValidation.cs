using ConsDTR.Runtime;
using ConsDTR.Runtime.Discovery;
using System.Collections.Immutable;

namespace ConsDTR.LAB.Host.Validation;

public static class DiscoveryValidation
{
    public static IReadOnlyList<string> RestoreChecks()
    {
        var passed = new List<string>();
        var options = new DirectoryOptions { MaxTargetsPerStep = 2, FairTargetsPerStep = 1,
            FairQuota = 1, StructuralQuota = 1, ExistingOrPinnedQuota = 1, MaxIntentSteps = 4 };
        var originalAudit = new List<JournalEvent>();
        var original = New(options, originalAudit);
        original.Upsert(new(0, 0, 0, 0, "worker0", "worker", true));
        original.Upsert(new(1, 0, 0, 0, "worker1", "worker", true));
        for (var id = 10; id < 13; id++) original.Upsert(new(id, 0, 0, 0, "v" + id, "value"));
        original.BeginEpoch(_ => ["value", "zzempty"], 100);
        var pin = original.NextPages(1).Single().Candidates.Single();
        original.PinExposed(0, pin, 1, 5);
        original.Upsert(new(11, 1, 1, 1, "changed", "value"));
        original.Remove(12);
        original.Upsert(new(13, 0, 0, 0, "added", "value"));
        var snapshot = original.ExportState();
        var roundtrip = DtrCanonical.Parse<CandidateDirectory.Snapshot>(DtrCanonical.Json(snapshot));
        var resumedAudit = originalAudit.ToList();
        var resumed = New(options, resumedAudit);
        resumed.RestoreState(roundtrip);
        Assert(DtrCanonical.Digest(snapshot) == DtrCanonical.Digest(resumed.ExportState()), "Resume changed directory state or restarted its fair epoch.");
        var step = 1;
        while (!original.Report.Complete)
        {
            step++;
            var expected = original.NextPages(step, [0], ["v10"]);
            var actual = resumed.NextPages(step, [0], ["v10"]);
            Assert(DtrCanonical.Json(expected) == DtrCanonical.Json(actual), "Resume changed next fair pages, tombstones, pins or quotas.");
        }
        Assert(DtrCanonical.Digest(original.ExportState()) == DtrCanonical.Digest(resumed.ExportState()), "Resume lost complete cursor/statistics state.");
        DirectoryAuditVerifier.Verify(resumedAudit);
        passed.Add("directory snapshot roundtrip preserves epoch queue, tombstones, pins, exact next pages and accounting");

        var invalid = New(options, []);
        void Reject(CandidateDirectory.Snapshot state)
        {
            var before = DtrCanonical.Digest(invalid.ExportState());
            ExpectInvalidResume(() => invalid.RestoreState(state));
            Assert(DtrCanonical.Digest(invalid.ExportState()) == before, "Rejected directory snapshot partially changed state.");
        }
        Reject(snapshot with { SchemaVersion = 2 });
        Reject(snapshot with { Options = options with { FairQuota = 2 } });
        Reject(snapshot with { EpochExposureBound = snapshot.EpochExposureBound + 1 });
        Reject(snapshot with { FairQueue = snapshot.FairQueue.Reverse().ToImmutableArray() });
        Reject(snapshot with { FairQueue = [] });
        Reject(snapshot with { Live = snapshot.Live.SetItem(0, snapshot.Live[0] with { Generation = -1 }) });
        Reject(snapshot with { EpochTargets = snapshot.EpochTargets.SetItem(0,
            snapshot.EpochTargets[0] with { Offset = snapshot.EpochTargets[0].Buckets[0].Entries.Length }) });
        Reject(snapshot with { Pins = snapshot.Pins.SetItem(0, snapshot.Pins[0] with { ExpiresAt = 99 }) });
        Reject(snapshot with { Pins = snapshot.Pins.SetItem(0, snapshot.Pins[0] with { ExpiresAt = snapshot.LastStep }) });
        var originalBefore = DtrCanonical.Digest(original.ExportState());
        ExpectInvalidResume(() => original.RestoreState(snapshot));
        Assert(DtrCanonical.Digest(original.ExportState()) == originalBefore, "A restore rewound an already advanced directory.");
        var failing = new CandidateDirectory(options, (_, _, _) => throw new InvalidOperationException("authority audit unavailable"));
        var failedBefore = DtrCanonical.Digest(failing.ExportState());
        ExpectInvalidResume(() => failing.RestoreState(snapshot));
        Assert(DtrCanonical.Digest(failing.ExportState()) == failedBefore, "Failed restore audit partially changed directory state.");
        var empty = New(options, []);
        empty.RestoreState(empty.ExportState());
        passed.Add("invalid schema/quota/identity/cursor/queue/pin/deadline and rewind snapshots reject atomically; restore audit precedes mutation");

        var originalIntent = new IntentState(4, (_, _, _) => { });
        originalIntent.Begin(1, 0, "HORN", [pin], 2, "write");
        Assert(originalIntent.Continue(2, "ROUTE", header => header == pin), "Expected valid bounded intent continuation.");
        var intentSnapshot = DtrCanonical.Parse<IntentState.Snapshot>(DtrCanonical.Json(originalIntent.ExportState()));
        var resumedIntent = new IntentState(4, (_, _, _) => { });
        resumedIntent.RestoreState(intentSnapshot, 2, header => header == pin);
        Assert(DtrCanonical.Digest(originalIntent.ExportState()) == DtrCanonical.Digest(resumedIntent.ExportState()), "Intent resume altered its anchors, stage or deadline.");
        originalIntent.Continue(3, "COMPUTE", _ => true); resumedIntent.Continue(3, "COMPUTE", _ => true);
        Assert(DtrCanonical.Digest(originalIntent.ExportState()) == DtrCanonical.Digest(resumedIntent.ExportState()), "Intent resume changed future lifecycle behavior.");
        void RejectIntent(IntentState.Snapshot state, long atStep = 2, Func<CandidateHeader, bool>? live = null)
        {
            var candidate = new IntentState(4, (_, _, _) => { });
            var before = DtrCanonical.Digest(candidate.ExportState());
            ExpectInvalidResume(() => candidate.RestoreState(state, atStep, live));
            Assert(DtrCanonical.Digest(candidate.ExportState()) == before, "Rejected intent resume partially changed state.");
        }
        RejectIntent(intentSnapshot, 5);
        RejectIntent(intentSnapshot, 2, _ => false);
        RejectIntent(intentSnapshot with { MaxIntentSteps = 5 });
        RejectIntent(intentSnapshot with { Current = intentSnapshot.Current! with { ExpiresAt = 100 } });
        RejectIntent(intentSnapshot with { Current = intentSnapshot.Current! with { CreatedAt = 3 } });
        RejectIntent(intentSnapshot with { Current = intentSnapshot.Current! with { Stage = "FACT" } });
        RejectIntent(intentSnapshot with { Current = intentSnapshot.Current! with { Anchors = [pin with { Generation = -1 }] } });
        var badAuditIntent = new IntentState(4, (_, _, _) => throw new InvalidOperationException("intent journal unavailable"));
        var badAuditBefore = DtrCanonical.Digest(badAuditIntent.ExportState());
        ExpectInvalidResume(() => badAuditIntent.RestoreState(intentSnapshot));
        Assert(DtrCanonical.Digest(badAuditIntent.ExportState()) == badAuditBefore, "Failed intent restore audit changed state.");
        resumedIntent.Abort("TEST_COMPLETE");
        var abortState = DtrCanonical.Digest(resumedIntent.ExportState());
        ExpectInvalidResume(() => resumedIntent.RestoreState(intentSnapshot, 3));
        Assert(DtrCanonical.Digest(resumedIntent.ExportState()) == abortState, "Restoring an old intent erased an observed abort.");
        passed.Add("intent resume preserves original deadline and stage; expired/stale/future/version-invalid intents and aborted history cannot be promoted");
        return passed;
    }

    public static IReadOnlyList<string> Run()
    {
        var passed = new List<string>();
        var configurations = 0;
        for (var targets = 0; targets <= 6; targets++)
        for (var count = 0; count <= 15; count++)
        for (var quota = 1; quota <= 4; quota++)
        {
            var audit = new List<JournalEvent>();
            var directory = New(new DirectoryOptions { MaxTargetsPerStep = 2, FairTargetsPerStep = 1,
                CandidatesPerTarget = 8, FairQuota = quota, StructuralQuota = 2, ExistingOrPinnedQuota = 2 }, audit);
            for (var i = 0; i < targets; i++) directory.Upsert(new(i, 0, 0, 0, "w" + i, "worker", true));
            for (var i = 0; i < count; i++) directory.Upsert(new(100 + i, 0, 0, 0, "v" + i, "value"));
            directory.BeginEpoch(_ => ["value", "empty"], 10_000);
            var bound = directory.Report.EpochExposureBound;
            var seen = new HashSet<(int, int)>();
            long step = 0;
            while (!directory.Report.Complete)
            {
                Assert(++step <= bound, "Fair epoch exceeded its declared bound.");
                foreach (var page in directory.NextPages(step))
                foreach (var candidate in page.Candidates) seen.Add((page.Target.Slot, candidate.Slot));
            }
            Assert(seen.Count == targets * count, "Every stable target/candidate identity must be exposed.");
            DirectoryAuditVerifier.Verify(audit);
            configurations++;
        }
        passed.Add($"fair target/bucket/empty scheduling: {configurations} configurations within T2a bound");
        {
            var audit = new List<JournalEvent>();
            var directory = New(new DirectoryOptions { FairQuota = 1, StructuralQuota = 1, ExistingOrPinnedQuota = 1 }, audit);
            var old = new CandidateHeader(10, 0, 0, 0, "old", "value");
            directory.Upsert(new(0, 0, 0, 0, "worker", "worker", true)); directory.Upsert(old);
            directory.Upsert(new(11, 0, 0, 0, "last", "value"));
            directory.BeginEpoch(_ => ["value"], 100);
            directory.Remove(10);
            directory.Upsert(old with { Generation = 1, BindingRevision = 1, MetadataRevision = 1, ObjectId = "new" });
            directory.Upsert(new(12, 0, 0, 0, "added", "value"));
            var first = directory.NextPages(1).Single();
            Assert(first.Candidates.Count == 0 && first.Tombstones == 1, "Changed versions consume tombstone scan quota.");
            Assert(directory.Report.Epoch == 1, "Metadata changes cannot restart epochs.");
            Assert(directory.NextPages(2).Single().Candidates.Single().Slot == 11, "Old membership must remain visible.");
            directory.BeginEpoch(_ => ["value"], 100);
            var next = new List<CandidateHeader>();
            for (var step = 3; !directory.Report.Complete; step++) next.AddRange(directory.NextPages(step).Single().Candidates);
            Assert(next.Select(h => h.Slot).Order().SequenceEqual(new[] { 10, 11, 12 }), "New entries join the next epoch.");
            DirectoryAuditVerifier.Verify(audit);
            passed.Add("epoch survives deletion/reset/registration; tombstones charged; next-epoch identities correct");
        }
        {
            var audit = new List<JournalEvent>();
            var directory = New(new DirectoryOptions { FairQuota = 1, StructuralQuota = 1, ExistingOrPinnedQuota = 1, MaxPins = 1 }, audit);
            directory.Upsert(new(0, 0, 0, 0, "worker", "worker", true));
            for (var i = 0; i < 4; i++) directory.Upsert(new(10 + i, 0, 0, 0, "v" + i, "value"));
            directory.BeginEpoch(_ => ["value"], 100);
            var exposed = directory.NextPages(1).Single().Candidates.Single();
            directory.PinExposed(0, exposed, 1, 3);
            var page = directory.NextPages(2, [0], ["v0"]).Single();
            Assert(page.Candidates.Any(c => c.Slot == 11) && page.Candidates.Count <= 3, "Pins/structure cannot consume fair quota.");
            directory.NextPages(3);
            Assert(directory.Report.PinInvalidations == 1, "Expired pins must be removed.");
            ExpectFailure(() => directory.PinExposed(0, new(999, 0, 0, 0, "hidden", "value"), 3, 4));
            while (!directory.Report.Complete) directory.NextPages(4);
            DirectoryAuditVerifier.Verify(audit);
            passed.Add("pin/structural quotas preserve fairness; unexposed and expired pins rejected");
        }
        {
            var audit = new List<JournalEvent>();
            var directory = New(new DirectoryOptions { MaxScanEntries = 1, FairQuota = 2 }, audit);
            directory.Upsert(new(0, 0, 0, 0, "worker", "worker", true));
            directory.Upsert(new(10, 0, 0, 0, "a", "value")); directory.Upsert(new(11, 0, 0, 0, "b", "value"));
            directory.BeginEpoch(_ => ["value"], 1);
            Assert(!directory.Report.FullEpochFitsBudget, "Incomplete coverage budget must be declared.");
            ExpectFailure(() => directory.NextPages(1));
            Assert(directory.Report.ScannedEntries == 0 && !directory.Report.Complete, "Failed metering cannot advance cursor.");
            ExpectFailure(() => directory.BeginEpoch(_ => ["value"], 100));
            passed.Add("exposure budget exhaustion preserves cursor; unfinished epochs cannot restart");
        }
        {
            var count = 0;
            var directory = new CandidateDirectory(new(), (_, _, _) => { if (++count > 1) throw new InvalidOperationException("journal full"); });
            directory.Upsert(new(0, 0, 0, 0, "worker", "worker", true));
            var candidate = new CandidateHeader(10, 0, 0, 0, "candidate", "value");
            ExpectFailure(() => directory.Upsert(candidate));
            Assert(!directory.IsLive(candidate), "Audit failure must precede state mutation.");
            passed.Add("authority audit failure cannot partially mutate directory");
        }
        return passed;
    }

    private static CandidateDirectory New(DirectoryOptions options, List<JournalEvent> journal) => new(options, (kind, payload, cost) =>
        journal.Add(new JournalEvent(journal.Count, Guid.Empty, 0, "Editing", "AUDIT", DtrCanonical.Json(new { Kind = kind,
            Data = DtrCanonical.Json(payload) }), 0, 0, 0, 0, "", "", "", "", cost, cost, ExecutionMode.LearnedOnly, cost)));
    private static void Assert(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static void ExpectFailure(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Expected invalid discovery operation to fail.");
    }

    private static void ExpectInvalidResume(Action action)
    {
        try { action(); } catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException) { return; }
        throw new InvalidOperationException("Expected invalid discovery resume input to fail.");
    }
}
