using ConsDTR.Lib.Inference;
using ConsDTR.Lib.Models;
using ConsDTR.Runtime;
using ConsDTR.Runtime.Tasks;
using static TorchSharp.torch;

namespace ConsDTR.LAB.Host.Validation;

public static class InferenceValidation
{
    public static IReadOnlyList<string> Run(string device = "cpu")
    {
        random.manual_seed(19);
        using var model = new DtrNeuralModel(new DtrModelConfig { HiddenDim = 16, RelationRank = 4 }, device);
        var task = new HornTask(new HornProblem(["A", "B"], ["A"], [new HornRule("ab", ["A"], "B")], "A"));
        var options = new DtrInferenceOptions
        {
            Editor = DtrEditorMode.Frozen, Deterministic = true,
            Runtime = new SessionOptions { WorkerCount = 2, RegisterCount = 6, MaxSlots = 32, MaxMicrosteps = 6,
                MaxWork = 20_000_000, MaxExportBytes = 2_000_000, FinalGateWorkAllowance = 1_000_000 }
        };
        var directory = Path.Combine(Environment.CurrentDirectory, ".artifacts", "session-validation", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "session.json");
        DtrSessionArchive? saved = null;
        var runner = new DtrInferenceRunner(model);
        var paused = runner.Run(task, options, checkpoint: context =>
        {
            if (context.Runtime.Snapshot().MicrostepId != 1) return false;
            saved = context.Save(path);
            return true;
        });
        Assert(paused.Status == DtrResultStatus.Unknown && paused.Reason == "CHECKPOINTED_FOR_EXCLUSIVE_RESUME" && saved is not null,
            "Checkpoint callback must stop at its completed exclusive cut.");
        using (var recovered = DtrSessionArchive.Load(path, saved!.ExpectedPrefixHead, model, task))
        {
            Assert(recovered.Directory.Report.Epoch == saved.Body.Directory.Epoch &&
                recovered.Directory.Report.ScannedEntries == saved.Body.Directory.ScannedEntries,
                "Recover must retain directory epoch and scan progress.");
            using var scope = NewDisposeScope();
            Assert(recovered.Hidden.cpu().data<float>().ToArray().Select(BitConverter.SingleToInt32Bits).SequenceEqual(saved.Body.HiddenFloatBits),
                "Float32 state must retain all bits on the declared device.");
            var cert = recovered.Runtime.Seal(HornTask.Atom("A"));
            Assert(IndependentVerifier.Verify(cert, task.CreateIndependentVerifier(), recovered.Runtime.TrustedEndAnchor, 1_000_000).Accepted,
                "A resumed complete prefix must still pass the independent formal gate.");
        }
        using (var continuation = DtrSessionArchive.Load(path, saved.ExpectedPrefixHead, model, task))
        {
            var final = runner.Run(task, resume: continuation);
            Assert(final.Status == DtrResultStatus.Unknown && final.Snapshot.RemainingMicrosteps == 0,
                "Resume consumes the original remaining microsteps rather than resetting budgets.");
        }
        ExpectFailure(() => { using var invalid = DtrSessionArchive.Load(path, "changed-trusted-head", model, task); });
        var changed = saved with { Body = saved.Body with { HiddenFloatBits = saved.Body.HiddenFloatBits.Select((bit, index) => index == 0 ? bit ^ 1 : bit).ToArray() } };
        var changedPath = Path.Combine(directory, "changed.json");
        File.WriteAllText(changedPath, DtrCanonical.Json(changed));
        ExpectFailure(() => { using var invalid = DtrSessionArchive.Load(changedPath, saved.ExpectedPrefixHead, model, task); });
        using var otherModel = new DtrNeuralModel(new DtrModelConfig { HiddenDim = 16, RelationRank = 4 }, device);
        ExpectFailure(() => { using var invalid = DtrSessionArchive.Load(path, saved.ExpectedPrefixHead, otherModel, task); });
        return ["full session preserves float32 state, directory epoch, intent/RNG and original budgets",
            "resumed genesis-to-cut prefix can pass an independently anchored formal gate",
            "changed session hidden state, different weights and substituted trusted head are rejected"];
    }

    private static void Assert(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static void ExpectFailure(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Expected changed session archive to be rejected.");
    }
}
