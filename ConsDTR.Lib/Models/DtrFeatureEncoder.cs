using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ConsDTR.Runtime;
using TorchSharp;
using static TorchSharp.torch;

namespace ConsDTR.Lib.Models;

/// <summary>Deterministic typed local encoding. Hash channels are latent features, never proof of equality.</summary>
public static class DtrFeatureEncoder
{
    public static DtrEncodedState EncodeSnapshot(RuntimeSnapshot snapshot, DtrModelConfig config, Device device)
    {
        config.Validate();
        var slots = snapshot.Slots.OrderBy(slot => slot.Handle.SlotId).ToArray();
        var records = snapshot.Records.ToDictionary(record => record.RecordId);
        var features = new float[slots.Length * config.FeatureDim];
        for (var index = 0; index < slots.Length; index++)
        {
            var slot = slots[index];
            var row = features.AsSpan(index * config.FeatureDim, config.FeatureDim);
            row[(int)slot.Kind] = 1;
            row[4] = slot.Active ? 1 : 0;
            row[5] = Squash(slot.Handle.Generation);
            row[6] = Squash(slot.BindingRevision);
            row[7] = Squash(slot.MetadataRevision);
            if (slot.RecordId is not { } recordId || !records.TryGetValue(recordId, out var record)) continue;
            row[8] = 1;
            row[9] = record.IsInput ? 1 : 0;
            row[10] = record.Value.Trust == ValueTrust.Verified ? 1 : -1;
            row[11] = Squash(record.ValueRevision);
            EncodeValue(record.Value, row[12..]);
        }
        return new DtrEncodedState(tensor(features, device: device).reshape(slots.Length, config.FeatureDim), slots);
    }

    public static Tensor EncodeGoal(TaskDefinition task, DtrModelConfig config, Device device)
    {
        var features = new float[config.FeatureDim];
        features[(int)SlotKind.Goal] = 1;
        features[4] = 1;
        EncodeValue(new ExactValue(task.TaskId, task.CanonicalQuery, ValueTrust.Candidate), features.AsSpan(12));
        return tensor(features, device: device);
    }

    /// <summary>Reuse recurrent state only for a still-live identical slot generation.</summary>
    public static Tensor SynchronizeHidden(DtrNeuralModel model, IReadOnlyList<SlotState>? previousSlots,
        Tensor? previousHidden, DtrEncodedState current, Tensor localEncoding)
    {
        using var scope = NewDisposeScope();
        var initial = model.InitializeHidden(localEncoding);
        if (previousSlots is null || previousHidden is null) return initial.MoveToOuterDisposeScope();
        if (previousHidden.shape[0] != previousSlots.Count) throw new ArgumentException("Previous slot/hidden count mismatch.");
        var previous = previousSlots.Select((slot, index) => (slot, index))
            .ToDictionary(pair => pair.slot.Handle, pair => pair);
        var previousIdentities = previousSlots.ToDictionary(slot => slot.Handle.SlotId);
        var rows = current.Slots.Select((slot, index) =>
        {
            if (previous.TryGetValue(slot.Handle, out var old) && old.slot.Active && slot.Active)
                return previousHidden.select(0, old.index).reshape(1, -1);
            if (previousIdentities.TryGetValue(slot.Handle.SlotId, out var priorIdentity) &&
                priorIdentity.Handle.Generation != slot.Handle.Generation)
                return zeros(1, model.Config.HiddenDim, device: model.Device); // RESET clears the old generation.
            return initial.select(0, index).reshape(1, -1);
        }).ToArray();
        return (rows.Length == 0 ? initial : cat(rows, 0)).MoveToOuterDisposeScope();
    }

    private static void EncodeValue(ExactValue value, Span<float> row)
    {
        // A complete local record is processed in O(UTF8 bytes), which the runtime must charge.
        var typeBytes = Encoding.UTF8.GetBytes(value.Type);
        var dataBytes = Encoding.UTF8.GetBytes(value.Data);
        var typeHash = SHA256.HashData(typeBytes);
        var dataHash = SHA256.HashData(dataBytes);
        row[0] = Squash(dataBytes.Length);
        row[1 + typeHash[0] % 4] = 1;
        if (long.TryParse(value.Data, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            row[5] = integer < 0 ? -1 : integer > 0 ? 1 : 0;
            row[6] = Squash(integer);
            row[7] = (float)((ulong)integer & 0xffff) / 65535;
        }
        for (var offset = 8; offset < row.Length; offset++) row[offset] = dataHash[(offset - 8) % 32] / 127.5f - 1;
    }

    private static float Squash(long value) => (float)(Math.Sign(value) * Math.Log(1 + Math.Abs((double)value)) / 32);
}
