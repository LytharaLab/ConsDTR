namespace ConsDTR.Lib.Models;

/// <summary>A serializable random stream for hard categorical actions; independent of Torch autograd.</summary>
public sealed class DtrRandomStream
{
    public ulong State { get; private set; }
    public long DrawCount { get; private set; }

    public DtrRandomStream(ulong seed = 1, long drawCount = 0)
    {
        State = seed == 0 ? 0x9e3779b97f4a7c15UL : seed;
        DrawCount = drawCount >= 0 ? drawCount : throw new ArgumentOutOfRangeException(nameof(drawCount));
    }

    public double NextDouble()
    {
        var value = State;
        value ^= value >> 12;
        value ^= value << 25;
        value ^= value >> 27;
        State = value;
        DrawCount++;
        return (unchecked(value * 2685821657736338717UL) >> 11) * (1.0 / (1UL << 53));
    }
}
