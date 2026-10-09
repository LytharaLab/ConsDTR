namespace ConsDTR.Runtime;

internal static class RuntimeAccounting
{
    // Work units are an explicit deterministic byte-processing tariff, not elapsed CPU/GPU time.
    public static long JournalWork(RuntimeState before, RuntimeState after, string payload) =>
        checked(32L + (DtrCanonical.Encode(before).LongLength + DtrCanonical.Encode(after).LongLength +
            DtrCanonical.EncodeJson(payload).LongLength + 63) / 64);
}
