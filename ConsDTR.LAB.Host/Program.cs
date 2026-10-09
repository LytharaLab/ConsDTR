namespace ConsDTR.LAB.Host;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var device = Option(args, "--device") ?? "cpu";
            if (args.Contains("--help"))
            {
                Console.WriteLine("ConsDTRv1: --demo | --train-local | --train-hard | --train-onpolicy [--updates 4] | --self-test | --evaluate [--checkpoint path] [--cases 10]");
                Console.WriteLine("Common: --device cpu|cuda:0|auto  --output path (default: workspace/run)");
                return 0;
            }
            Console.WriteLine($"ConsDTRv1 / Draft-2; requested device: {device}; working directory: {Environment.CurrentDirectory}");
            if (args.Contains("--train-onpolicy"))
            {
                var result = Lib.Training.DtrOnPolicyExperiment.Run(device, int.Parse(Option(args, "--updates") ?? "4"),
                    Path.Combine(Output(args), "onpolicy-training"));
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            else if (args.Contains("--train-hard"))
            {
                var result = Lib.Training.DtrHornTeacherExperiment.Run(device, int.Parse(Option(args, "--updates") ?? "10"),
                    Path.Combine(Output(args), "hard-training-checkpoint"));
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            else if (args.Contains("--train-local"))
            {
                var result = Lib.Training.DtrLocalTrainingExperiment.Run(device, int.Parse(Option(args, "--updates") ?? "20"));
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("课程 A/B 局部训练示例；此结果不代表自主 LearnedOnly 能力或性能验收。");
            }
            else if (args.Contains("--self-test"))
            {
                var count = 0;
                foreach (var suite in new[] { "RuntimeValidation", "TaskValidation", "DiscoveryValidation", "NeuralValidation", "InferenceValidation", "EvaluationValidation", "TrainingValidation" })
                {
                    Console.WriteLine($"Running {suite}...");
                    var type = typeof(Program).Assembly.GetType("ConsDTR.LAB.Host.Validation." + suite)
                        ?? throw new InvalidOperationException("Missing validation suite: " + suite);
                    var run = type.GetMethod("Run")!;
                    var values = (IReadOnlyList<string>)run.Invoke(null, run.GetParameters().Length == 0 ? null : [device])!;
                    foreach (var value in values) Console.WriteLine("PASS " + value);
                    count += values.Count;
                    if (suite == "DiscoveryValidation")
                    {
                        var restores = (IReadOnlyList<string>)type.GetMethod("RestoreChecks")!.Invoke(null, null)!;
                        foreach (var value in restores) Console.WriteLine("PASS " + value);
                        count += restores.Count;
                    }
                }
                foreach (var value in Lib.Evaluation.DtrEvaluationValidation.Run())
                {
                    Console.WriteLine("PASS " + value);
                    count++;
                }
                Console.WriteLine($"All {count} validation groups passed.");
            }
            else if (args.Contains("--evaluate"))
                Examples.DtrExample.RunEvaluation(device, Option(args, "--checkpoint"), int.Parse(Option(args, "--cases") ?? "10"), Output(args));
            else Examples.DtrExample.Run(device, Output(args));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.GetBaseException());
            return 1;
        }
    }

    private static string? Option(string[] args, string key)
    {
        var index = Array.IndexOf(args, key);
        if (index < 0) return null;
        if (index + 1 >= args.Length) throw new ArgumentException("Missing value for " + key);
        return args[index + 1];
    }

    private static string Output(string[] args) => Path.GetFullPath(Option(args, "--output") ?? Path.Combine(Environment.CurrentDirectory, "run"));
}
