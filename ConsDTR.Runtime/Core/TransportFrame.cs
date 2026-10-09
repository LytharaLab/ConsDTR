namespace ConsDTR.Runtime;

public sealed record TransportFrame(long SnapshotSeq, long TopologyRevision, IReadOnlyList<ReadReceipt> ReadReceipts, IReadOnlyList<WriteReceipt> WriteReceipts)
{
    public IReadOnlyList<ReadReceipt> ForWorker(SlotHandle worker) => ReadReceipts.Where(x => x.Receiver == worker).ToArray();
    public WriteReceipt? WriteFor(SlotHandle worker, SlotHandle target) => WriteReceipts.SingleOrDefault(x => x.Worker == worker && x.Target == target);
}
