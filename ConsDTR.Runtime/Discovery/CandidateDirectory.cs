using System.Collections.Immutable;

namespace ConsDTR.Runtime.Discovery;

/// <summary>
/// Three quota-separated metadata paths. Epoch membership is frozen: updates produce
/// tombstones and join the next epoch, never restart an unfinished fair scan.
/// </summary>
public sealed class CandidateDirectory
{
    private readonly DirectoryOptions _options;
    private readonly Action<string, object, long> _audit;
    private Dictionary<int, CandidateHeader> _live = [];
    private Dictionary<string, SortedSet<int>> _objects = new(StringComparer.Ordinal);
    private Queue<TargetCursor> _fair = [];
    private List<TargetCursor> _epochTargets = [];
    private List<Pin> _pins = [];
    private HashSet<CandidateHeader> _exposed = [];
    private long _epoch;
    private long _bound;
    private long _snapshotEntries;
    private long _scanned;
    private long _tombstones;
    private long _pinHits;
    private long _pinInvalidations;
    private bool _fits;
    private long _buildPauseSteps;
    private long _epochRemainingMicrosteps;
    private long _lastStep;

    public CandidateDirectory(DirectoryOptions options, Action<string, object, long> audit)
    {
        options.Validate();
        _options = options;
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public ExposureReport Report => new(_epoch, _bound, _fits, _snapshotEntries,
        _scanned, _tombstones, _pinHits, _pinInvalidations, _fair.Count == 0);

    /// <summary>Pure state export. The archive caller meters serialization and binds these bytes to its trusted runtime prefix.</summary>
    public Snapshot ExportState() => new(1, _options with { }, _epoch, _bound, _fits, _snapshotEntries,
        _scanned, _tombstones, _pinHits, _pinInvalidations, _buildPauseSteps, _epochRemainingMicrosteps, _lastStep,
        _live.Values.OrderBy(header => header.Slot).ToImmutableArray(),
        _epochTargets.Select(cursor => new CursorSnapshot(cursor.Target,
            cursor.Buckets.Select(bucket => new BucketState(bucket.Key, bucket.Entries.ToImmutableArray())).ToImmutableArray(),
            cursor.BucketIndex, cursor.Offset)).ToImmutableArray(),
        _fair.Select(cursor => cursor.Target.Slot).ToImmutableArray(),
        _pins.Select(pin => new PinState(pin.Target, pin.Header, pin.CreatedAt, pin.ExpiresAt)).ToImmutableArray(),
        _exposed.OrderBy(header => header.Slot).ThenBy(header => header.Generation).ThenBy(header => header.BindingRevision)
            .ThenBy(header => header.MetadataRevision).ThenBy(header => header.ObjectId, StringComparer.Ordinal)
            .ThenBy(header => header.Bucket, StringComparer.Ordinal).ThenBy(header => header.Active).ThenBy(header => header.IsTarget).ToImmutableArray());

    /// <summary>Restores an externally authenticated resume input without beginning a new epoch. Invalid input or failed audit changes nothing.</summary>
    public void RestoreState(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateSnapshot(snapshot);
        if (snapshot.LastStep < _lastStep || snapshot.Epoch < _epoch || snapshot.ScannedEntries < _scanned ||
            snapshot.Tombstones < _tombstones || snapshot.PinHits < _pinHits || snapshot.PinInvalidations < _pinInvalidations)
            throw new InvalidOperationException("A directory restore cannot roll back observed time, progress or accounting.");
        if (_fair.Count != 0 && snapshot.Epoch != _epoch)
            throw new InvalidOperationException("Restoring an unfinished directory cannot replace its exposure epoch.");
        if (_epoch > 0 && snapshot.Epoch == _epoch)
        {
            if (snapshot.EpochTargets.Length != _epochTargets.Count || snapshot.BuildPauseSteps != _buildPauseSteps ||
                snapshot.EpochRemainingMicrosteps != _epochRemainingMicrosteps)
                throw new InvalidOperationException("The same epoch must retain its original frozen target membership and bound.");
            for (var index = 0; index < _epochTargets.Count; index++)
            {
                var current = _epochTargets[index]; var restored = snapshot.EpochTargets[index];
                if (current.Target != restored.Target || current.Buckets.Length != restored.Buckets.Length ||
                    current.Buckets.Where((bucket, bucketIndex) => bucket.Key != restored.Buckets[bucketIndex].Key ||
                        !bucket.Entries.SequenceEqual(restored.Buckets[bucketIndex].Entries)).Any() ||
                    restored.BucketIndex < current.BucketIndex || (restored.BucketIndex == current.BucketIndex && restored.Offset < current.Offset))
                    throw new InvalidOperationException("A directory restore cannot rewrite epoch membership or rewind a cursor.");
            }
        }
        var live = snapshot.Live.ToDictionary(header => header.Slot);
        var objects = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
        foreach (var header in snapshot.Live)
        {
            if (_live.TryGetValue(header.Slot, out var old) && (header.Generation < old.Generation ||
                header.BindingRevision < old.BindingRevision || header.MetadataRevision < old.MetadataRevision))
                throw new InvalidOperationException("A directory restore cannot roll back an observed identity revision.");
            if (!objects.TryGetValue(header.ObjectId, out var ids)) objects.Add(header.ObjectId, ids = []);
            ids.Add(header.Slot);
        }
        var targets = snapshot.EpochTargets.Select(cursor => new TargetCursor(cursor.Target,
            cursor.Buckets.Select(bucket => new BucketSnapshot(bucket.Key, bucket.Entries.ToArray())).ToArray(), cursor.BucketIndex, cursor.Offset)).ToArray();
        var byTarget = targets.ToDictionary(cursor => cursor.Target.Slot);
        var queue = new Queue<TargetCursor>(snapshot.FairQueue.Select(slot => byTarget[slot]));
        var pins = snapshot.Pins.Select(pin => new Pin(pin.Target, pin.Header, pin.CreatedAt, pin.ExpiresAt)).ToList();
        var targetList = targets.ToList(); var exposed = snapshot.Exposed.ToHashSet();
        _audit("DIRECTORY_STATE_RESTORE", new { SchemaVersion = snapshot.SchemaVersion, Digest = DtrCanonical.Digest(snapshot) },
            checked(1 + snapshot.Live.Length + snapshot.SnapshotEntries + snapshot.Exposed.Length + snapshot.Pins.Length));
        _live = live; _objects = objects; _epochTargets = targetList; _fair = queue; _pins = pins; _exposed = exposed;
        _epoch = snapshot.Epoch; _bound = snapshot.EpochExposureBound; _fits = snapshot.FullEpochFitsBudget;
        _snapshotEntries = snapshot.SnapshotEntries; _scanned = snapshot.ScannedEntries; _tombstones = snapshot.Tombstones;
        _pinHits = snapshot.PinHits; _pinInvalidations = snapshot.PinInvalidations; _buildPauseSteps = snapshot.BuildPauseSteps;
        _epochRemainingMicrosteps = snapshot.EpochRemainingMicrosteps; _lastStep = snapshot.LastStep;
    }

    public void Upsert(CandidateHeader header)
    {
        ValidateHeader(header);
        if (_live.TryGetValue(header.Slot, out var old) && old == header) return;
        if (old is null && _live.Count >= _options.MaxSnapshotEntries)
            throw new InvalidOperationException("EXPOSURE_LIVE_INDEX_CAPACITY_EXHAUSTED");
        if (old is not null && (header.Generation < old.Generation || header.BindingRevision < old.BindingRevision || header.MetadataRevision < old.MetadataRevision))
            throw new ArgumentException("Public metadata revisions cannot move backwards.", nameof(header));
        _audit("DIRECTORY_UPSERT", header, 1);
        if (old is not null) RemoveIndex(old);
        _live[header.Slot] = header;
        if (!_objects.TryGetValue(header.ObjectId, out var ids))
            _objects[header.ObjectId] = ids = [];
        ids.Add(header.Slot);
    }

    public void Remove(int slot)
    {
        if (!_live.TryGetValue(slot, out var old)) return;
        _audit("DIRECTORY_REMOVE", old, 1);
        _live.Remove(slot);
        RemoveIndex(old);
    }

    public bool IsLive(CandidateHeader header) =>
        _live.TryGetValue(header.Slot, out var current) && current == header;

    /// <param name="compatibleBuckets">Public type compatibility, never semantic readiness.</param>
    public void BeginEpoch(Func<CandidateHeader, IReadOnlyList<string>> compatibleBuckets,
        long remainingMicrosteps, long buildPauseSteps = 1)
    {
        if (_fair.Count != 0) throw new InvalidOperationException("An unfinished epoch cannot restart.");
        if (remainingMicrosteps < 0 || buildPauseSteps < 0) throw new ArgumentOutOfRangeException();
        var buckets = _live.Values.GroupBy(x => x.Bucket, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.OrderBy(h => h.Slot).ToArray(), StringComparer.Ordinal);
        var targets = _live.Values.Where(x => x.IsTarget).OrderBy(x => x.Slot).ToArray();
        var cursors = targets.Select(t => new TargetCursor(t,
            compatibleBuckets(t).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .Select(key => new BucketSnapshot(key, buckets.GetValueOrDefault(key) ?? [])).ToArray())).ToArray();
        // An empty compatibility list is still one metered empty bucket service.
        foreach (var cursor in cursors)
            if (cursor.Buckets.Length == 0) cursor.Buckets = [new BucketSnapshot("EMPTY", [])];
        var entries = checked(cursors.Sum(t => t.Buckets.Sum(b => (long)b.Entries.Length)) + targets.Length);
        if (entries > _options.MaxSnapshotEntries)
            throw new InvalidOperationException("EXPOSURE_SNAPSHOT_CAPACITY_EXHAUSTED");
        var maxPages = cursors.Length == 0 ? 0 : cursors.Max(t => t.Buckets.Sum(b =>
            Math.Max(1L, (b.Entries.LongLength + _options.FairQuota - 1) / _options.FairQuota)));
        var rounds = (targets.LongLength + _options.FairTargetsPerStep - 1) / _options.FairTargetsPerStep;
        var bound = checked(buildPauseSteps + rounds * maxPages);
        var newEpoch = checked(_epoch + 1);
        _audit("DIRECTORY_EPOCH_BEGIN", new
        {
            Epoch = newEpoch, Options = _options, Targets = cursors.Select(c => new { c.Target, c.Buckets }).ToArray(),
            EpochExposureBound = bound, BuildPauseSteps = buildPauseSteps,
            FullEpochFitsBudget = bound <= remainingMicrosteps, SnapshotEntries = entries
        }, checked(entries + 1));
        foreach (var cursor in cursors) _fair.Enqueue(cursor);
        _epochTargets.Clear(); _epochTargets.AddRange(cursors);
        _epoch = newEpoch;
        _bound = bound;
        _fits = bound <= remainingMicrosteps;
        _snapshotEntries = entries;
        _buildPauseSteps = buildPauseSteps;
        _epochRemainingMicrosteps = remainingMicrosteps;
        _exposed.Clear();
    }

    /// <summary>Intent targets/keys come from prior logged actions or a public fixed rule.</summary>
    public IReadOnlyList<DirectoryPage> NextPages(long step,
        IReadOnlyList<int>? priorIntentTargets = null,
        IReadOnlyList<string>? priorStructuralKeys = null,
        Func<int, IReadOnlyList<int>>? existingNeighbors = null)
    {
        if (step < _lastStep) throw new ArgumentOutOfRangeException(nameof(step), "Directory time cannot move backwards.");
        ExpirePins(step);
        _lastStep = step;
        var selected = new List<(CandidateHeader Target, TargetCursor? Cursor)>();
        // Fair positions are reserved before the discretionary positions.
        var serviceCount = Math.Min(_options.FairTargetsPerStep, _fair.Count);
        var nextFair = _fair.Take(serviceCount).ToArray();
        selected.AddRange(nextFair.Select(c => (c.Target, (TargetCursor?)c)));
        foreach (var targetId in priorIntentTargets ?? Array.Empty<int>())
        {
            if (selected.Count == _options.MaxTargetsPerStep) break;
            if (_live.TryGetValue(targetId, out var target) && target.IsTarget &&
                selected.All(x => x.Target.Slot != targetId)) selected.Add((target, null));
        }

        var pages = new List<DirectoryPage>();
        foreach (var (target, cursor) in selected)
        {
            var candidates = new List<CandidateHeader>();
            var inspected = 0;
            var dead = 0;
            var fairEntries = cursor is null ? [] : cursor.CurrentEntries(_options.FairQuota);
            foreach (var entry in fairEntries)
            {
                inspected++;
                if (!IsLive(entry)) dead++;
                else if (entry.Slot != target.Slot && IsLive(target)) AddUnique(candidates, entry);
            }
            // Empty buckets and tombstone targets consume service/cost too.
            var emptyCost = cursor is not null && fairEntries.Length == 0 ? 1 : 0;
            var structuralCount = 0;
            foreach (var key in (priorStructuralKeys ?? Array.Empty<string>()).Take(_options.StructuralQuota))
            {
                inspected++;
                if (!_objects.TryGetValue(key, out var ids)) continue;
                foreach (var id in ids.Take(_options.StructuralQuota - structuralCount))
                {
                    inspected++;
                    structuralCount++;
                    if (id != target.Slot && IsLive(target)) AddUnique(candidates, _live[id]);
                }
                if (structuralCount == _options.StructuralQuota) break;
            }
            var retained = 0;
            foreach (var pin in _pins.Where(p => p.Target == target.Slot).Take(_options.ExistingOrPinnedQuota))
            {
                inspected++;
                retained++;
                if (IsLive(pin.Header) && IsLive(target)) AddUnique(candidates, pin.Header);
                else dead++;
            }
            foreach (var id in (existingNeighbors?.Invoke(target.Slot) ?? Array.Empty<int>())
                         .Take(_options.ExistingOrPinnedQuota - retained))
            {
                inspected++;
                if (_live.TryGetValue(id, out var h) && IsLive(target) && id != target.Slot)
                    AddUnique(candidates, h);
                else dead++;
            }
            if (checked(_scanned + inspected + emptyCost) > _options.MaxScanEntries)
                throw new InvalidOperationException("EXPOSURE_BUDGET_EXHAUSTED");
            var page = new DirectoryPage(target, candidates.AsReadOnly(), _epoch,
                inspected + emptyCost, dead, cursor is not null);
            _audit("DIRECTORY_PAGE", new
            {
                Step = step, Page = page, FairBucket = cursor?.CurrentBucket,
                FairOffset = cursor?.Offset, FrozenEntries = fairEntries,
                StructuralKeys = (priorStructuralKeys ?? Array.Empty<string>()).Take(_options.StructuralQuota).ToArray()
            }, checked(inspected + emptyCost + candidates.Count + 1));
            _scanned += inspected + emptyCost;
            _tombstones += dead;
            foreach (var h in candidates) _exposed.Add(h);
            if (cursor is not null)
            {
                if (_fair.Dequeue() != cursor) throw new InvalidOperationException("Fair service order changed.");
                cursor.Advance(_options.FairQuota);
                if (!cursor.Complete) _fair.Enqueue(cursor);
            }
            pages.Add(page);
        }
        return pages.AsReadOnly();
    }

    public void PinExposed(int target, CandidateHeader header, long step, long intentExpiresAt)
    {
        if (step < _lastStep) throw new ArgumentOutOfRangeException(nameof(step), "Directory time cannot move backwards.");
        if (!_exposed.Contains(header) || !IsLive(header) || !_live.TryGetValue(target, out var t) || !t.IsTarget)
            throw new InvalidOperationException("Pins require an already exposed live identity.");
        var expiry = Math.Min(intentExpiresAt, checked(step + _options.MaxIntentSteps));
        if (expiry <= step) throw new ArgumentOutOfRangeException(nameof(intentExpiresAt));
        _audit("CANDIDATE_PIN", new { Target = target, Header = header, ExpiresAt = expiry }, 1);
        _lastStep = step;
        _pins.RemoveAll(p => p.Target == target && p.Header.Slot == header.Slot);
        if (_options.MaxPins == 0) return;
        while (_pins.Count >= _options.MaxPins) _pins.RemoveAt(0);
        _pins.Add(new Pin(target, header, step, expiry));
    }

    public void AbortPins(int target)
    {
        _audit("CANDIDATE_PIN_ABORT", new { Target = target }, 1);
        _pins.RemoveAll(p => p.Target == target);
    }

    private void ExpirePins(long step)
    {
        var invalid = _pins.Where(p => p.ExpiresAt <= step || !IsLive(p.Header)).ToArray();
        _audit("CANDIDATE_PIN_CHECK", new { Step = step, Removed = invalid }, _pins.Count + 1);
        _pinInvalidations += invalid.Length;
        _pinHits += _pins.Count - invalid.Length;
        foreach (var pin in invalid) _pins.Remove(pin);
    }

    private void RemoveIndex(CandidateHeader header)
    {
        var ids = _objects[header.ObjectId];
        ids.Remove(header.Slot);
        if (ids.Count == 0) _objects.Remove(header.ObjectId);
    }

    private static void AddUnique(List<CandidateHeader> entries, CandidateHeader header)
    {
        if (entries.All(x => x.Slot != header.Slot)) entries.Add(header);
    }

    private static void ValidateHeader(CandidateHeader header)
    {
        if (header is null || header.Slot < 0 || header.Generation < 0 || header.BindingRevision < 0 || header.MetadataRevision < 0 ||
            string.IsNullOrEmpty(header.Bucket) || string.IsNullOrEmpty(header.ObjectId) || (header.IsTarget && !header.Active))
            throw new ArgumentException("Invalid public metadata identity, revision or accessibility.", nameof(header));
    }

    private void ValidateSnapshot(Snapshot snapshot)
    {
        if (snapshot.SchemaVersion != 1 || snapshot.Options != _options || snapshot.Epoch < 0 || snapshot.EpochExposureBound < 0 ||
            snapshot.SnapshotEntries < 0 || snapshot.SnapshotEntries > _options.MaxSnapshotEntries || snapshot.ScannedEntries < 0 ||
            snapshot.ScannedEntries > _options.MaxScanEntries || snapshot.Tombstones < 0 || snapshot.Tombstones > snapshot.ScannedEntries ||
            snapshot.PinHits < 0 || snapshot.PinInvalidations < 0 || snapshot.BuildPauseSteps < 0 || snapshot.EpochRemainingMicrosteps < 0 || snapshot.LastStep < 0 ||
            snapshot.Live.IsDefault || snapshot.EpochTargets.IsDefault || snapshot.FairQueue.IsDefault || snapshot.Pins.IsDefault || snapshot.Exposed.IsDefault ||
            snapshot.Live.Length > _options.MaxSnapshotEntries || snapshot.EpochTargets.Length > _options.MaxSnapshotEntries ||
            snapshot.Pins.Length > _options.MaxPins || snapshot.Exposed.Length > snapshot.ScannedEntries)
            throw new ArgumentException("Invalid directory snapshot schema, options, capacity or statistics.", nameof(snapshot));
        snapshot.Options.Validate();
        if (snapshot.EpochTargets.Any(cursor => cursor is null || cursor.Target is null || cursor.Buckets.IsDefault || cursor.Buckets.Any(bucket => bucket is null)) ||
            snapshot.Pins.Any(pin => pin is null))
            throw new ArgumentException("Null directory snapshot entry.", nameof(snapshot));
        foreach (var header in snapshot.Live.Concat(snapshot.Exposed)) ValidateHeader(header);
        if (!snapshot.Live.Select(header => header.Slot).SequenceEqual(snapshot.Live.Select(header => header.Slot).Distinct().Order()) ||
            snapshot.Exposed.Distinct().Count() != snapshot.Exposed.Length || snapshot.FairQueue.Distinct().Count() != snapshot.FairQueue.Length ||
            !snapshot.EpochTargets.Select(cursor => cursor.Target.Slot).SequenceEqual(snapshot.EpochTargets.Select(cursor => cursor.Target.Slot).Distinct().Order()))
            throw new ArgumentException("Duplicate or noncanonical directory snapshot identities.", nameof(snapshot));
        long entries = 0; long maxPages = 0; long servicedScans = 0;
        var taken = new List<long>(); var totalPages = new List<long>();
        foreach (var cursor in snapshot.EpochTargets)
        {
            ValidateHeader(cursor.Target);
            if (!cursor.Target.IsTarget || cursor.Buckets.IsDefaultOrEmpty || cursor.BucketIndex < 0 || cursor.BucketIndex > cursor.Buckets.Length || cursor.Offset < 0 ||
                !cursor.Buckets.Select(bucket => bucket.Key).SequenceEqual(cursor.Buckets.Select(bucket => bucket.Key).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)))
                throw new ArgumentException("Invalid frozen target or bucket cursor.", nameof(snapshot));
            long pages = 0; long consumed = 0;
            var memberSlots = new HashSet<int>();
            for (var index = 0; index < cursor.Buckets.Length; index++)
            {
                var bucket = cursor.Buckets[index];
                if (string.IsNullOrEmpty(bucket.Key) || bucket.Entries.IsDefault ||
                    !bucket.Entries.Select(header => header.Slot).SequenceEqual(bucket.Entries.Select(header => header.Slot).Distinct().Order()))
                    throw new ArgumentException("Invalid frozen bucket membership/order.", nameof(snapshot));
                foreach (var header in bucket.Entries)
                {
                    ValidateHeader(header);
                    if (header.Bucket != bucket.Key || !memberSlots.Add(header.Slot))
                        throw new ArgumentException("Frozen entries are in incompatible or repeated buckets.", nameof(snapshot));
                }
                var bucketPages = Math.Max(1L, (bucket.Entries.Length + _options.FairQuota - 1L) / _options.FairQuota);
                pages = checked(pages + bucketPages);
                entries = checked(entries + bucket.Entries.Length);
                if (index < cursor.BucketIndex)
                { consumed = checked(consumed + bucketPages); servicedScans = checked(servicedScans + Math.Max(1, bucket.Entries.Length)); }
            }
            if (cursor.BucketIndex == cursor.Buckets.Length)
            {
                if (cursor.Offset != 0) throw new ArgumentException("Completed cursor retains an offset.", nameof(snapshot));
            }
            else
            {
                var length = cursor.Buckets[cursor.BucketIndex].Entries.Length;
                if ((length == 0 && cursor.Offset != 0) || (length > 0 && cursor.Offset >= length) || cursor.Offset % _options.FairQuota != 0)
                    throw new ArgumentException("Cursor offset skips a fair page or does not match its quota.", nameof(snapshot));
                consumed = checked(consumed + cursor.Offset / _options.FairQuota);
                servicedScans = checked(servicedScans + cursor.Offset);
            }
            taken.Add(consumed); totalPages.Add(pages); maxPages = Math.Max(maxPages, pages); entries = checked(entries + 1);
        }
        var bound = checked(snapshot.BuildPauseSteps + ((snapshot.EpochTargets.Length + _options.FairTargetsPerStep - 1L) / _options.FairTargetsPerStep) * maxPages);
        if (entries != snapshot.SnapshotEntries || entries > _options.MaxSnapshotEntries || servicedScans > snapshot.ScannedEntries || bound != snapshot.EpochExposureBound ||
            snapshot.FullEpochFitsBudget != (snapshot.Epoch > 0 && snapshot.EpochExposureBound <= snapshot.EpochRemainingMicrosteps) ||
            (snapshot.Epoch == 0 && (snapshot.EpochTargets.Length != 0 || snapshot.BuildPauseSteps != 0 || snapshot.SnapshotEntries != 0)))
            throw new ArgumentException("False exposure accounting or epoch bound in directory snapshot.", nameof(snapshot));
        // Reconstruct only the bounded fair service order, not the directory or a task solution.
        var expectedQueue = new Queue<int>(Enumerable.Range(0, snapshot.EpochTargets.Length));
        var consumedPages = new long[taken.Count];
        var remaining = taken.Sum();
        while (remaining > 0)
        {
            if (expectedQueue.Count == 0) throw new ArgumentException("Fair queue has impossible progress.", nameof(snapshot));
            var index = expectedQueue.Dequeue();
            if (taken[index] == 0) throw new ArgumentException("A fair target was skipped in the restored epoch.", nameof(snapshot));
            taken[index]--; consumedPages[index]++; remaining--;
            if (consumedPages[index] < totalPages[index]) expectedQueue.Enqueue(index);
        }
        if (!snapshot.FairQueue.SequenceEqual(expectedQueue.Select(index => snapshot.EpochTargets[index].Target.Slot)))
            throw new ArgumentException("Restored fair queue is reordered, incomplete or contains a completed cursor.", nameof(snapshot));
        var exposed = snapshot.Exposed.ToHashSet();
        var pinPairs = new HashSet<(int, int)>();
        foreach (var pin in snapshot.Pins)
        {
            ValidateHeader(pin.Header);
            if (pin.Target < 0 || pin.CreatedAt < 0 || pin.CreatedAt > snapshot.LastStep || pin.ExpiresAt <= snapshot.LastStep ||
                pin.ExpiresAt <= pin.CreatedAt || pin.ExpiresAt - pin.CreatedAt > _options.MaxIntentSteps || !exposed.Contains(pin.Header) ||
                !pinPairs.Add((pin.Target, pin.Header.Slot)))
                throw new ArgumentException("Restored pins are stale, unexposed, duplicated or exceed their original finite lifetime.", nameof(snapshot));
        }
    }

    public sealed record Snapshot(int SchemaVersion, DirectoryOptions Options, long Epoch, long EpochExposureBound,
        bool FullEpochFitsBudget, long SnapshotEntries, long ScannedEntries, long Tombstones, long PinHits, long PinInvalidations,
        long BuildPauseSteps, long EpochRemainingMicrosteps, long LastStep, ImmutableArray<CandidateHeader> Live,
        ImmutableArray<CursorSnapshot> EpochTargets, ImmutableArray<int> FairQueue, ImmutableArray<PinState> Pins, ImmutableArray<CandidateHeader> Exposed);
    public sealed record BucketState(string Key, ImmutableArray<CandidateHeader> Entries);
    public sealed record CursorSnapshot(CandidateHeader Target, ImmutableArray<BucketState> Buckets, int BucketIndex, int Offset);
    public sealed record PinState(int Target, CandidateHeader Header, long CreatedAt, long ExpiresAt);

    private sealed record Pin(int Target, CandidateHeader Header, long CreatedAt, long ExpiresAt);
    private sealed record BucketSnapshot(string Key, CandidateHeader[] Entries);
    private sealed class TargetCursor(CandidateHeader target, BucketSnapshot[] buckets, int bucketIndex = 0, int offset = 0)
    {
        public CandidateHeader Target { get; } = target;
        public BucketSnapshot[] Buckets { get; set; } = buckets;
        public int BucketIndex { get; private set; } = bucketIndex;
        public int Offset { get; private set; } = offset;
        public bool Complete => BucketIndex >= Buckets.Length;
        public string CurrentBucket => Buckets[BucketIndex].Key;
        public CandidateHeader[] CurrentEntries(int quota) =>
            Buckets[BucketIndex].Entries.Skip(Offset).Take(quota).ToArray();
        public void Advance(int quota)
        {
            Offset += Math.Min(quota, Buckets[BucketIndex].Entries.Length - Offset);
            if (Offset >= Buckets[BucketIndex].Entries.Length) { BucketIndex++; Offset = 0; }
        }
    }
}
