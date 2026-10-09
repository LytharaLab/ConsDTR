using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Text.Json.Serialization;

namespace ConsDTR.Lib.Models;

public static class DtrCheckpointStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>Bind runtime sessions to the actual shared parameters and neural configuration.</summary>
    public static string ComputeModelDigest(DtrNeuralModel model)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true)) model.save(writer);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(model.Config)));
        hash.AppendData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static DtrCheckpointManifest Save(DtrNeuralModel model, string directory,
        DtrCheckpointManifest? manifest = null)
    {
        Directory.CreateDirectory(directory);
        var weightsPath = Path.Combine(directory, "weights.dat");
        model.save(weightsPath);
        var saved = (manifest ?? new DtrCheckpointManifest()) with
        {
            Model = model.Config,
            WeightsSha256 = Digest(weightsPath)
        };
        saved.Validate();
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(saved, JsonOptions));
        return saved;
    }

    public static (DtrNeuralModel Model, DtrCheckpointManifest Manifest) Load(string directory,
        string device = "cpu", bool freezeForInference = true)
    {
        var manifestJson = File.ReadAllText(Path.Combine(directory, "manifest.json"));
        using var document = JsonDocument.Parse(manifestJson);
        RequireFields(document.RootElement, ["CheckpointId", "Model", "WeightsSha256", "KernelSemanticVersion",
            "InputTypeSchemaVersion", "JournalSchemaVersion", "TrainingRecipe", "DataSplitFingerprint",
            "CompletedUpdates", "CapabilityValidated"]);
        RequireFields(document.RootElement.GetProperty("Model"), ["ArchitectureId", "SpecRevision", "PolicySchemaVersion", "HiddenDim",
            "RelationRank", "FeatureDim", "RelationTypes", "ScoreHiddenDim", "HiddenBound", "OperationClasses",
            "PortClasses", "MaximumSequenceLength", "ParameterDtype"]);
        RequireFields(document.RootElement.GetProperty("TrainingRecipe"), ["Optimizer", "LearningRate", "ValueLossWeight",
            "EntropyWeight", "GradientNormClip", "MaximumEpisodeSteps", "TruncatedBpttSteps", "BatchSize",
            "MaximumInputSlots", "Seed", "TeacherVersion", "SplitFingerprint", "CurriculumStage", "CertificateReward",
            "StepCostWeight", "EditCostWeight", "ExactCheckCostWeight", "ExactDataCostWeight"]);
        var manifest = JsonSerializer.Deserialize<DtrCheckpointManifest>(manifestJson, JsonOptions) ??
            throw new InvalidDataException("Missing checkpoint manifest.");
        manifest.Validate();
        var weights = Path.Combine(directory, "weights.dat");
        if (!string.Equals(manifest.WeightsSha256, Digest(weights), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Checkpoint weights do not match their manifest digest.");
        var model = new DtrNeuralModel(manifest.Model, device);
        try
        {
            model.load(weights);
            model.ParameterVersion = manifest.CompletedUpdates;
            foreach (var parameter in model.parameters()) DtrNeuralModel.EnsureFinite(parameter, "loaded parameter");
            if (freezeForInference) model.FreezeForInference();
            return (model, manifest);
        }
        catch { model.Dispose(); throw; }
    }

    public static string Digest(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void RequireFields(JsonElement objectValue, string[] fields)
    {
        if (objectValue.ValueKind != JsonValueKind.Object || fields.Any(field => !objectValue.TryGetProperty(field, out _)))
            throw new InvalidDataException("Required manifest fields must be explicitly declared; defaults do not replace archival metadata.");
        if (objectValue.EnumerateObject().GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new InvalidDataException("Duplicate checkpoint manifest fields are ambiguous.");
    }
}
