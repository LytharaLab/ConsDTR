using System.Globalization;
using System.Text.Json;
using ConsDTR.Lib.Models;
using ConsDTR.Runtime;
using ConsDTR.Runtime.Discovery;
using static TorchSharp.torch;

namespace ConsDTR.Lib.Inference;

/// <summary>Full prefix and recurrent state; requires an independently retained trusted head.</summary>
public sealed record DtrSessionArchive(DtrSessionArchive.State Body, IReadOnlyList<JournalEvent> RuntimeJournal, string ExpectedPrefixHead)
{
    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(false, true);
    public sealed record State(int SchemaVersion, string ModelDigest, string InferenceOptionsJson,
        int HiddenDim, int[] HiddenFloatBits, IReadOnlyList<SlotState> NeuralSlots,
        CandidateDirectory.Snapshot Directory, IntentState.Snapshot Intent,
        string ActionRandomState, long ActionRandomDrawCount, string EditorRandomState, long EditorRandomDrawCount);

    public static DtrSessionArchive Save(DtrInferenceCheckpointContext context, string path)
    {
        if (context.Runtime.IsSealed || context.Runtime.Journal[^1].Phase != "Idle")
            throw new InvalidOperationException("Archive requires a completed, unsealed microstep.");
        var cap = context.Runtime.Options.MaxExportBytes;
        context.Runtime.RecordAudit("SESSION_ARCHIVE_ALLOWANCE", new { MaxBytes = cap }, checked(1 + cap / 64));
        using var scope = NewDisposeScope();
        DtrNeuralModel.EnsureFinite(context.Hidden, "archived state");
        if (context.Hidden.abs().max().item<float>() > context.Model.Config.HiddenBound)
            throw new InvalidDataException("Archived state exceeds its configured bound.");
        var bits = context.Hidden.cpu().data<float>().ToArray().Select(BitConverter.SingleToInt32Bits).ToArray();
        var body = new State(1, DtrCheckpointStore.ComputeModelDigest(context.Model), JsonSerializer.Serialize(context.Options),
            context.Model.Config.HiddenDim, bits, context.NeuralSlots.ToArray(), context.Directory.ExportState(), context.Intent.ExportState(),
            context.ActionRandom.State.ToString("X16"), context.ActionRandom.DrawCount, context.EditorRandom.State.ToString("X16"), context.EditorRandom.DrawCount);
        context.Runtime.RecordAudit("SESSION_ARCHIVE_BINDING", new { StateDigest = DtrCanonical.Digest(body), SchemaVersion = 1 }, 1);
        var prefix = context.Runtime.Journal;
        var archive = new DtrSessionArchive(body, prefix, prefix[^1].EventDigest);
        var json = DtrCanonical.Json(archive);
        if (StrictUtf8.GetByteCount(json) > cap) throw new RuntimeResourceException("Session archive exceeds export capacity.");
        var absolute = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        var temporary = absolute + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, json, StrictUtf8);
        File.Move(temporary, absolute, overwrite: true);
        return archive;
    }

    public static DtrInferenceResume Load(string path, string independentlyExpectedPrefixHead,
        DtrNeuralModel model, IKernelTask task, long maxBytes = 128_000_000, long replayAllowance = 10_000_000)
    {
        if (maxBytes < 1 || new FileInfo(path).Length > maxBytes) throw new RuntimeResourceException("Archive byte capacity exceeded.");
        var archive = DtrCanonical.Parse<DtrSessionArchive>(File.ReadAllText(path, StrictUtf8));
        if (archive.Body.SchemaVersion != 1 || archive.RuntimeJournal.Count == 0 || archive.ExpectedPrefixHead != independentlyExpectedPrefixHead ||
            archive.RuntimeJournal[^1].EventDigest != independentlyExpectedPrefixHead)
            throw new InvalidDataException("Archive does not match the independently expected prefix head.");
        var binding = archive.RuntimeJournal[^1];
        if (binding.EventKind != "AUDIT") throw new InvalidDataException("Missing authority state binding.");
        using var payload = JsonDocument.Parse(binding.CanonicalPayload);
        if (payload.RootElement.GetProperty("Kind").GetString() != "SESSION_ARCHIVE_BINDING")
            throw new InvalidDataException("Archive cut does not bind a complete snapshot.");
        using var data = JsonDocument.Parse(payload.RootElement.GetProperty("Data").GetString()!);
        if (data.RootElement.GetProperty("StateDigest").GetString() != DtrCanonical.Digest(archive.Body))
            throw new InvalidDataException("Neural/directory/intent/RNG snapshot changed after the trusted cut.");
        var state = archive.Body;
        if (state.ModelDigest != DtrCheckpointStore.ComputeModelDigest(model) || state.HiddenDim != model.Config.HiddenDim)
            throw new InvalidDataException("Archive belongs to another model checkpoint.");
        var options = JsonSerializer.Deserialize<DtrInferenceOptions>(state.InferenceOptionsJson, DtrCanonical.JsonOptions)
            ?? throw new InvalidDataException("Missing inference configuration.");
        if (state.HiddenFloatBits.Length != checked(state.NeuralSlots.Count * state.HiddenDim) || state.NeuralSlots.Count > options.Runtime.MaxSlots ||
            state.NeuralSlots.Select(s => s.Handle).Distinct().Count() != state.NeuralSlots.Count)
            throw new InvalidDataException("Invalid recurrent shape or identities.");
        var values = state.HiddenFloatBits.Select(BitConverter.Int32BitsToSingle).ToArray();
        if (values.Any(value => !float.IsFinite(value) || Math.Abs(value) > model.Config.HiddenBound)) throw new InvalidDataException("Invalid recurrent numeric state.");
        var runtime = RuntimeSession.Recover(task, archive.RuntimeJournal, independentlyExpectedPrefixHead, replayAllowance);
        try
        {
            var live = runtime.Snapshot().Slots;
            if (state.NeuralSlots.Any(s => !live.Any(t => t.Handle == s.Handle && t.Kind == s.Kind && t.Active == s.Active)))
                throw new InvalidDataException("Recurrent state uses a different recovered generation.");
            var directory = new CandidateDirectory(options.Directory, runtime.RecordAudit);
            directory.RestoreState(state.Directory);
            var intent = new IntentState(options.Directory.MaxIntentSteps, runtime.RecordAudit);
            intent.RestoreState(state.Intent, runtime.Snapshot().MicrostepId, directory.IsLive);
            return new(runtime, tensor(values, device: model.Device).reshape(state.NeuralSlots.Count, state.HiddenDim), state.NeuralSlots, directory, intent,
                new DtrRandomStream(ulong.Parse(state.ActionRandomState, NumberStyles.HexNumber, CultureInfo.InvariantCulture), state.ActionRandomDrawCount),
                new DtrRandomStream(ulong.Parse(state.EditorRandomState, NumberStyles.HexNumber, CultureInfo.InvariantCulture), state.EditorRandomDrawCount), options);
        }
        catch { runtime.Dispose(); throw; }
    }
}
