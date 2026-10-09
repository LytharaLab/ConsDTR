namespace ConsDTR.Runtime.Discovery;

/// <summary>Finite learned intent lifecycle; this state never plans edges or creates facts.</summary>
public sealed class IntentState
{
    private readonly int maxIntentSteps;
    private readonly Action<string, object, long> audit;
    private long lastStep;
    public Intent? Current { get; private set; }
    public long Aborts { get; private set; }

    public IntentState(int maxIntentSteps, Action<string, object, long> audit)
    {
        if (maxIntentSteps < 1) throw new ArgumentOutOfRangeException(nameof(maxIntentSteps));
        this.maxIntentSteps = maxIntentSteps;
        this.audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public Snapshot ExportState() => new(1, maxIntentSteps, Copy(Current), Aborts, lastStep);

    /// <summary>Restore metadata from a trusted resume prefix. Expired/stale intents are rejected, never renewed.</summary>
    public void RestoreState(Snapshot snapshot, long? resumeStep = null, Func<CandidateHeader, bool>? isLive = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var step = resumeStep ?? snapshot.LastStep;
        if (snapshot.SchemaVersion != 1 || snapshot.MaxIntentSteps != maxIntentSteps || snapshot.Aborts < Aborts ||
            snapshot.LastStep < lastStep || snapshot.LastStep < 0 || step < snapshot.LastStep || snapshot.Aborts < 0)
            throw new ArgumentException("Invalid intent snapshot version, lifetime configuration, time or accounting.", nameof(snapshot));
        var next = Copy(snapshot.Current);
        if (Current is not null && snapshot.Aborts == Aborts && (next is null || next.Worker != Current.Worker ||
            next.Target != Current.Target || next.OperationFamily != Current.OperationFamily || next.WritePort != Current.WritePort ||
            next.CreatedAt != Current.CreatedAt || next.ExpiresAt != Current.ExpiresAt || !next.Anchors.SequenceEqual(Current.Anchors)))
            throw new InvalidOperationException("Replacing an active intent requires its previously recorded abort.");
        if (next is not null)
        {
            Validate(next);
            if (next.CreatedAt > snapshot.LastStep || next.ExpiresAt <= step || (isLive is not null && next.Anchors.Any(header => !isLive(header))))
                throw new InvalidOperationException("An expired, future or stale intent cannot be restored or renewed.");
            if (Current is not null && next.CreatedAt == Current.CreatedAt && next.ExpiresAt != Current.ExpiresAt)
                throw new InvalidOperationException("Restoring the same intent cannot change its original deadline.");
        }
        audit("INTENT_STATE_RESTORE", new { SchemaVersion = snapshot.SchemaVersion, Digest = DtrCanonical.Digest(snapshot with { Current = next }), ResumeStep = step },
            checked(1L + (next?.Anchors.Count ?? 0)));
        Current = next; Aborts = snapshot.Aborts; lastStep = step;
    }

    public void Begin(long step, int worker, string operationFamily,
        IReadOnlyList<CandidateHeader> alreadyExposed, int target, string port)
    {
        if (Current is not null || step < lastStep) throw new InvalidOperationException("Invalid intent lifecycle or regressing time.");
        ArgumentNullException.ThrowIfNull(alreadyExposed);
        var intent = new Intent(worker, operationFamily, Array.AsReadOnly(alreadyExposed.ToArray()), target, port,
            step, checked(step + maxIntentSteps), "PREPARE");
        Validate(intent);
        audit("INTENT_BEGIN", intent, alreadyExposed.Count + 1);
        Current = intent;
        lastStep = step;
    }

    public bool Continue(long step, string stage, Func<CandidateHeader, bool> isLive)
    {
        if (step < lastStep) throw new ArgumentOutOfRangeException(nameof(step), "Intent time cannot move backwards.");
        ArgumentNullException.ThrowIfNull(isLive);
        if (Current is null) return false;
        if (step >= Current.ExpiresAt || Current.Anchors.Any(h => !isLive(h)))
        {
            Abort("EXPIRED_OR_STALE");
            lastStep = step;
            return false;
        }
        if (stage is not ("PREPARE" or "ROUTE" or "COMPUTE" or "COMMIT_OR_ABORT"))
            throw new ArgumentException("Unknown intent stage.", nameof(stage));
        var next = Current with { Stage = stage };
        audit("INTENT_CONTINUE", next, next.Anchors.Count + 1);
        Current = next;
        lastStep = step;
        return true;
    }

    public void Abort(string reason)
    {
        if (Current is null) return;
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var aborts = checked(Aborts + 1);
        audit("INTENT_ABORT", new { Intent = Current, Reason = reason }, 1);
        Current = null;
        Aborts = aborts;
    }

    private static Intent? Copy(Intent? intent) => intent is null ? null : intent with
        { Anchors = Array.AsReadOnly((intent.Anchors ?? throw new ArgumentException("Missing intent anchors.")).ToArray()) };

    private void Validate(Intent intent)
    {
        if (intent.Worker < 0 || intent.Target < 0 || string.IsNullOrWhiteSpace(intent.OperationFamily) || intent.OperationFamily.Length > 128 ||
            string.IsNullOrWhiteSpace(intent.WritePort) || intent.WritePort.Length > 64 || intent.CreatedAt < 0 || intent.ExpiresAt <= intent.CreatedAt ||
            intent.ExpiresAt - intent.CreatedAt > maxIntentSteps || intent.Stage is not ("PREPARE" or "ROUTE" or "COMPUTE" or "COMMIT_OR_ABORT") ||
            intent.Anchors.Any(header => header is null) || intent.Anchors.Select(header => header.Slot).Distinct().Count() != intent.Anchors.Count)
            throw new ArgumentException("Invalid finite intent metadata.", nameof(intent));
        foreach (var header in intent.Anchors)
            if (header is null || header.Slot < 0 || header.Generation < 0 || header.BindingRevision < 0 || header.MetadataRevision < 0 ||
                string.IsNullOrEmpty(header.ObjectId) || string.IsNullOrEmpty(header.Bucket) || (header.IsTarget && !header.Active))
                throw new ArgumentException("Invalid intent anchor identity.", nameof(intent));
    }

    public sealed record Snapshot(int SchemaVersion, int MaxIntentSteps, Intent? Current, long Aborts, long LastStep);

    public sealed record Intent(int Worker, string OperationFamily,
        IReadOnlyList<CandidateHeader> Anchors, int Target, string WritePort,
        long CreatedAt, long ExpiresAt, string Stage);
}
