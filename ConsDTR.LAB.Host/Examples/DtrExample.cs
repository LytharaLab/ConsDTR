using System.Text.Json;
using ConsDTR.Lib.Inference;
using ConsDTR.Lib.Models;
using ConsDTR.Runtime;
using ConsDTR.Runtime.Tasks;

namespace ConsDTR.LAB.Host.Examples;

/// <summary>Structured inputs, frozen neural execution and independently verified certificates.</summary>
public static class DtrExample
{
    public static void Run(string device, string outputDirectory)
    {
        TorchSharp.torch.random.manual_seed(7);
        using var model = new DtrNeuralModel(new DtrModelConfig { HiddenDim = 32, RelationRank = 8 }, device);
        Console.WriteLine($"Actual neural device: {model.Device}; shared parameters: {model.ParameterCount}");
        Directory.CreateDirectory(outputDirectory);
        var options = Options();
        var executor = new DtrTaskPlanExecutor(model);
        var horn = new HornProblem(["A", "B", "C"], ["A"],
            [new HornRule("r1", ["A"], "B"), new HornRule("r2", ["B"], "C")], "C");
        Save("horn-positive", executor.RunHornFallback(horn, options), outputDirectory);
        var negative = new HornProblem(["A", "B", "C"], ["A"], [new HornRule("r1", ["A"], "B")], "C");
        Save("horn-negative", executor.RunHornFallback(negative, options), outputDirectory);
        var path = new GraphPathProblem(["s", "m", "t"], [new DirectedEdge("sm", "s", "m"), new DirectedEdge("mt", "m", "t")], "s", "t");
        Save("path-witness", executor.Execute(new ReachabilityTask(path), charge => TaskTeacherPlans.PathWitness(path, ["sm", "mt"], charge),
            options with { Mode = ExecutionMode.OracleDiagnostic }), outputDirectory);
        var csp = new CspProblem([new CspVariable("x", [0, 1]), new CspVariable("y", [0, 1])], [FiniteConstraint.NotEqual("xy", "x", "y")]);
        Save("csp-witness", executor.Execute(new FiniteCspTask(csp), charge => TaskTeacherPlans.AssignmentWitness(csp,
            new Dictionary<string, int> { ["x"] = 0, ["y"] = 1 }, charge), options with { Mode = ExecutionMode.OracleDiagnostic }), outputDirectory);
        var learned = new DtrInferenceRunner(model).Run(new HornTask(horn), new DtrInferenceOptions
        {
            Runtime = options with { MaxMicrosteps = 16, Mode = ExecutionMode.LearnedOnly }, Deterministic = true
        });
        Save("untrained-learned-only", learned, outputDirectory);
        DtrCheckpointStore.Save(model, Path.Combine(outputDirectory, "untrained-checkpoint"));
        Console.WriteLine("FairFallback 与 OracleDiagnostic 的证书演示不计为 LearnedOnly 成绩。权威锚请从可信运行时单独保存和取得。");
    }

    public static void RunEvaluation(string device, string? checkpoint, int cases, string outputDirectory)
    {
        if (cases < 1 || cases > 10_000) throw new ArgumentOutOfRangeException(nameof(cases));
        using var model = checkpoint is null ? new DtrNeuralModel(new DtrModelConfig { HiddenDim = 32, RelationRank = 8 }, device) : DtrCheckpointStore.Load(checkpoint, device).Model;
        var results = new List<object>();
        for (var seed = 0; seed < 5; seed++)
        foreach (var scale in new[] { 1, 2, 4 })
        foreach (var editor in Enum.GetValues<DtrEditorMode>())
        {
            var accepted = 0;
            long scanned = 0, work = 0, rejects = 0;
            double milliseconds = 0;
            for (var index = 0; index < cases; index++)
            {
                var depth = 2 * scale;
                var atoms = Enumerable.Range(0, depth + 2).Select(i => $"a{seed}_{index}_{i}").ToArray();
                var rules = Enumerable.Range(0, depth).Select(i => new HornRule($"r{i}", [atoms[i]], atoms[i + 1])).ToArray();
                var problem = new HornProblem(atoms, [atoms[0]], rules, index % 2 == 0 ? atoms[depth] : atoms[^1]);
                var result = new DtrInferenceRunner(model).Run(new HornTask(problem), new DtrInferenceOptions
                {
                    Runtime = Options() with { MaxMicrosteps = 32, Mode = ExecutionMode.LearnedOnly },
                    Editor = editor, Deterministic = false, SamplingSeed = (ulong)(seed * cases + index + 1)
                });
                if (result.IsVerified) accepted++;
                scanned += result.Exposure?.ScannedEntries ?? 0;
                work += Options().MaxWork - result.Snapshot.RemainingWork;
                rejects += result.RejectedActions;
                milliseconds += result.ElapsedMilliseconds;
            }
            results.Add(new { SamplingSeed = seed, Scale = scale, Editor = editor.ToString(), Cases = cases,
                CertifiedCoverage = (double)accepted / cases, ScannedEntries = scanned, RejectedActions = rejects,
                DeclaredWorkSum = work, TotalMilliseconds = milliseconds, Checkpoint = checkpoint ?? "UNTRAINED",
                TrainingSeed = "NOT_A_TRAINING_SEED", FixedGraphFeasibility = "NOT_DIAGNOSED" });
        }
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, "evaluation.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Saved evaluation.json to {outputDirectory}. Five sampling seeds are distinct from five independently trained checkpoints.");
    }

    public static SessionOptions Options() => new()
    {
        WorkerCount = 2, RegisterCount = 24, MaxSlots = 64, MaxMicrosteps = 128,
        MaxWork = 10_000_000, FinalGateWorkAllowance = 1_000_000
    };

    private static void Save(string name, DtrInferenceResult result, string directory)
    {
        Console.WriteLine($"{name}: {result.Status}; Mode={result.Mode}; {result.Reason}");
        if (result.Certificate is not null)
        {
            File.WriteAllText(Path.Combine(directory, name + ".certificate.json"), result.Certificate.Serialize());
            File.WriteAllText(Path.Combine(directory, name + ".expected-anchor.json"), DtrCanonical.Json(result.ExpectedEndAnchor
                ?? throw new InvalidOperationException("No independently returned runtime anchor.")));
        }
        File.WriteAllText(Path.Combine(directory, name + ".summary.json"), JsonSerializer.Serialize(new
        {
            Status = result.Status.ToString(), Mode = result.Mode.ToString(), result.Reason, result.RejectedActions,
            result.AcceptedEdits, result.ElapsedMilliseconds, result.Exposure, result.Verification,
            result.Snapshot.RemainingWork, result.Snapshot.RemainingMicrosteps
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (name != "untrained-learned-only" && !result.IsVerified)
            throw new InvalidOperationException("The executable example failed independent verification: " + result.Reason);
    }
}
