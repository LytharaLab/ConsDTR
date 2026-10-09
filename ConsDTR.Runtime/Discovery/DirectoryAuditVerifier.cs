using System.Text.Json;

namespace ConsDTR.Runtime.Discovery;

/// <summary>Independent replay of fair metadata pages, identity versions and unbroken epochs.</summary>
public static class DirectoryAuditVerifier
{
    public static void Verify(IEnumerable<JournalEvent> journal)
    {
        var live = new Dictionary<int, CandidateHeader>();
        var queue = new Queue<Cursor>();
        DirectoryOptions? options = null;
        long epoch = 0;
        long scanned = 0;
        foreach (var entry in journal.Where(e => e.EventKind == "AUDIT"))
        {
            using var outer = JsonDocument.Parse(entry.CanonicalPayload);
            var kind = outer.RootElement.GetProperty("Kind").GetString();
            if (kind is null || !kind.StartsWith("DIRECTORY_", StringComparison.Ordinal)) continue;
            using var data = JsonDocument.Parse(outer.RootElement.GetProperty("Data").GetString()!);
            var item = data.RootElement;
            switch (kind)
            {
                case "DIRECTORY_UPSERT":
                    var header = Read<CandidateHeader>(item);
                    live[header.Slot] = header;
                    break;
                case "DIRECTORY_REMOVE":
                    var removed = Read<CandidateHeader>(item);
                    if (!live.Remove(removed.Slot, out var previous) || previous != removed)
                        throw new InvalidDataException("Invalid directory removal identity.");
                    break;
                case "DIRECTORY_EPOCH_BEGIN":
                    if (queue.Count != 0) throw new InvalidDataException("Fair epoch was restarted before completion.");
                    options = Read<DirectoryOptions>(item.GetProperty("Options"));
                    options.Validate();
                    if (item.GetProperty("Epoch").GetInt64() != ++epoch)
                        throw new InvalidDataException("Non-contiguous directory epoch.");
                    long snapshots = 0;
                    long maxPages = 0;
                    var targetIds = new HashSet<int>();
                    foreach (var target in item.GetProperty("Targets").EnumerateArray())
                    {
                        var t = Read<CandidateHeader>(target.GetProperty("Target"));
                        if (!live.TryGetValue(t.Slot, out var current) || current != t || !t.IsTarget || !targetIds.Add(t.Slot))
                            throw new InvalidDataException("Invalid fair target snapshot.");
                        var buckets = target.GetProperty("Buckets").EnumerateArray().Select(b => new Bucket(
                            b.GetProperty("Key").GetString()!, Read<CandidateHeader[]>(b.GetProperty("Entries")))).ToArray();
                        if (buckets.Length == 0) throw new InvalidDataException("Missing empty bucket service.");
                        foreach (var bucket in buckets)
                        {
                            var expected = live.Values.Where(h => h.Bucket == bucket.Key).OrderBy(h => h.Slot).ToArray();
                            if (!expected.SequenceEqual(bucket.Entries)) throw new InvalidDataException("Snapshot omitted or changed bucket metadata.");
                        }
                        var pages = buckets.Sum(b => Math.Max(1L, (b.Entries.LongLength + options.FairQuota - 1) / options.FairQuota));
                        maxPages = Math.Max(maxPages, pages);
                        snapshots = checked(snapshots + 1 + buckets.Sum(b => b.Entries.LongLength));
                        queue.Enqueue(new Cursor(t, buckets));
                    }
                    if (!targetIds.SetEquals(live.Values.Where(h => h.IsTarget).Select(h => h.Slot)))
                        throw new InvalidDataException("Fair target starvation in snapshot.");
                    var bound = checked(item.GetProperty("BuildPauseSteps").GetInt64() +
                        ((targetIds.Count + options.FairTargetsPerStep - 1L) / options.FairTargetsPerStep) * maxPages);
                    if (item.GetProperty("EpochExposureBound").GetInt64() != bound ||
                        item.GetProperty("SnapshotEntries").GetInt64() != snapshots || snapshots > options.MaxSnapshotEntries)
                        throw new InvalidDataException("False directory exposure bound or capacity.");
                    break;
                case "DIRECTORY_PAGE":
                    if (options is null) throw new InvalidDataException("Page precedes epoch genesis.");
                    var page = Read<DirectoryPage>(item.GetProperty("Page"));
                    if (page.Epoch != epoch || page.Candidates.Count > options.CandidatesPerTarget ||
                        page.Candidates.Select(h => h.Slot).Distinct().Count() != page.Candidates.Count ||
                        page.Candidates.Any(h => !live.TryGetValue(h.Slot, out var h2) || h != h2))
                        throw new InvalidDataException("Invalid bounded directory page.");
                    if (page.IsFairService)
                    {
                        if (queue.Count == 0) throw new InvalidDataException("Extra fair service.");
                        var cursor = queue.Dequeue();
                        if (page.Target != cursor.Target || item.GetProperty("FairBucket").GetString() != cursor.Buckets[cursor.BucketIndex].Key ||
                            item.GetProperty("FairOffset").GetInt32() != cursor.Offset)
                            throw new InvalidDataException("Fair target skipped or reordered.");
                        var expected = cursor.Buckets[cursor.BucketIndex].Entries.Skip(cursor.Offset).Take(options.FairQuota).ToArray();
                        if (!expected.SequenceEqual(Read<CandidateHeader[]>(item.GetProperty("FrozenEntries"))))
                            throw new InvalidDataException("Fair page skipped entries or tombstones.");
                        if (page.ScannedEntries < Math.Max(1, expected.Length))
                            throw new InvalidDataException("Fair scan cost omitted.");
                        cursor.Offset += expected.Length;
                        if (cursor.Offset >= cursor.Buckets[cursor.BucketIndex].Entries.Length)
                        { cursor.BucketIndex++; cursor.Offset = 0; }
                        if (cursor.BucketIndex < cursor.Buckets.Length) queue.Enqueue(cursor);
                    }
                    scanned = checked(scanned + page.ScannedEntries);
                    if (scanned > options.MaxScanEntries) throw new InvalidDataException("Directory budget bypass.");
                    break;
            }
        }
    }

    private static T Read<T>(JsonElement value) => JsonSerializer.Deserialize<T>(value.GetRawText(), DtrCanonical.JsonOptions)
        ?? throw new InvalidDataException("Null directory audit.");
    private sealed record Bucket(string Key, CandidateHeader[] Entries);
    private sealed class Cursor(CandidateHeader target, Bucket[] buckets)
    {
        public CandidateHeader Target { get; } = target;
        public Bucket[] Buckets { get; } = buckets;
        public int BucketIndex { get; set; }
        public int Offset { get; set; }
    }
}
