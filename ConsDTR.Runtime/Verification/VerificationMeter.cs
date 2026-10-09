namespace ConsDTR.Runtime;

public sealed class VerificationMeter
{
    public long Limit { get; }
    public long UsedWork { get; private set; }
    public long RemainingWork => Limit - UsedWork;
    public bool Exhausted { get; private set; }
    public VerificationMeter(long limit) => Limit = limit >= 0 ? limit : throw new ArgumentOutOfRangeException(nameof(limit));
    public bool Charge(long work)
    {
        if (work < 0) throw new ArgumentOutOfRangeException(nameof(work));
        if (work > RemainingWork) { Exhausted = true; return false; }
        UsedWork += work;
        return true;
    }
}
